using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GenshinLyrePlayer.Models;
using GenshinLyrePlayer.Services;

namespace GenshinLyrePlayer.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly Player _player = new();

    public MainWindowViewModel()
    {
        _player.PlayheadChanged += p => Dispatcher.UIThread.Post(() =>
        {
            Playhead = p;
            if (Duration > 0 && Playhead >= Duration) { /* will finish */ }
        });
        _player.Finished += () => Dispatcher.UIThread.Post(() =>
        {
            IsPlaying = false;
            CountdownText = string.Empty;
        });
        _player.CountdownTick += sec => Dispatcher.UIThread.Post(() =>
        {
            CountdownText = sec > 0 ? $"准备演奏: {sec}" : string.Empty;
        });
    }

    [ObservableProperty] private IReadOnlyList<Note>? _notes;
    [ObservableProperty] private string? _fileName;
    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private double _duration;
    [ObservableProperty] private double _playhead;
    [ObservableProperty] private int _transpose;
    [ObservableProperty] private double _pixelsPerSecond = 120;
    [ObservableProperty] private int _countdownSeconds = 3;
    [ObservableProperty] private double _speed = 1.0;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string? _countdownText;
    [ObservableProperty] private string? _statusText = "就绪";

    /// <summary>可选的乐器列表（下拉框数据源）。</summary>
    public IReadOnlyList<Instrument> AvailableInstruments { get; } = Instruments.All;

    /// <summary>当前选中的乐器。切换时会用新映射重新计算所有音符的可演奏状态。</summary>
    [ObservableProperty] private Instrument _selectedInstrument = Instruments.Default;

    /// <summary>播放按钮应显示的文字，随 IsPlaying 自动变化（绑定用）。</summary>
    public string PlayPauseButtonText => IsPlaying ? "⏸ 暂停 (F8)" : "▶ 播放 (F8)";
    public string PlayPauseButtonTooltip => IsPlaying
        ? "暂停演奏，保留当前播放位置，再次按可继续(全局热键 F8)"
        : "开始 / 继续演奏。也可使用全局热键 F8（焦点在原神里也生效）";

    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(PlayPauseButtonText));
        OnPropertyChanged(nameof(PlayPauseButtonTooltip));
    }
    [ObservableProperty] private int _totalNotes;
    [ObservableProperty] private int _supportedNotes;
    [ObservableProperty] private int _unsupportedNotes;

    public string PlayheadText => FormatTime(Playhead) + " / " + FormatTime(Duration);

    partial void OnPlayheadChanged(double value) => OnPropertyChanged(nameof(PlayheadText));
    partial void OnDurationChanged(double value) => OnPropertyChanged(nameof(PlayheadText));

    partial void OnTransposeChanged(int value)
    {
        ReapplyMapping();
    }

    partial void OnSelectedInstrumentChanged(Instrument value)
    {
        ReapplyMapping();
        if (value != null)
            StatusText = $"已切换到 {value.Name}（{value.Description}）";
    }

    /// <summary>基于当前 Transpose + SelectedInstrument 重算 Key / Supported。</summary>
    private void ReapplyMapping()
    {
        if (Notes is null || Notes.Count == 0) return;
        MidiParser.ApplyTranspose(Notes, Transpose, SelectedInstrument);
        // 重新赋一个新的 List 引用，触发 UI 重绘
        Notes = new List<Note>(Notes);
        RefreshStats();
    }

    partial void OnSpeedChanged(double value) => _player.Speed = value;

    public async Task LoadMidiAsync(string path)
    {
        try
        {
            // 切换曲谱前先停止演奏并清理上一次的曲谱引用，避免内存持续增长。
            // （若不清理，Player 内部 Task 闭包与控件绑定仍可能引用旧音符列表。）
            _player.Stop();
            IsPlaying = false;
            CountdownText = string.Empty;
            Notes = null;
            TotalNotes = 0;
            SupportedNotes = 0;
            UnsupportedNotes = 0;
            Playhead = 0;
            Duration = 0;
            // 主动触发一次回收，尽快释放大型音符集合
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            StatusText = "正在解析…";
            var result = await Task.Run(() => MidiParser.Parse(path));
            FilePath = path;
            FileName = result.FileName;
            Duration = result.TotalDuration;
            Playhead = 0;

            // 避免调用两次 ReapplyMapping：若当前 Transpose 非 0，赋 0 会自动触发 OnTransposeChanged。
            if (Transpose != 0)
            {
                Notes = result.Notes;
                Transpose = 0; // 触发 OnTransposeChanged → ReapplyMapping
            }
            else
            {
                Notes = result.Notes;
                ReapplyMapping();
            }
            StatusText = $"已加载 {result.Notes.Count} 个音符，时长 {FormatTime(Duration)}";
        }
        catch (Exception ex)
        {
            StatusText = $"加载失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private void AutoTranspose()
    {
        if (Notes is null || Notes.Count == 0) return;
        var notesList = Notes.ToList();
        int total = notesList.Count;

        // 先看当前乐器的最佳移调能达成多高的可演奏率；若仍有未覆盖的音，
        // 则尝试所有乐器，挑覆盖最多者。
        var inCurrent = MidiParser.FindBestTransposeWithScore(notesList, SelectedInstrument);

        if (inCurrent.Score >= total)
        {
            // 当前乐器即可完整演奏
            Transpose = inCurrent.Shift;
            StatusText = $"已自动移调 {Format(inCurrent.Shift)} 半音（{SelectedInstrument.Name}，全部 {total} 音均可演奏）";
            return;
        }

        var best = MidiParser.FindBestTransposeAcrossInstruments(notesList, AvailableInstruments, SelectedInstrument);

        // 如果跨乐器的最佳得分不比当前乐器高，就沿用当前乐器的结果
        if (best.Score <= inCurrent.Score)
        {
            Transpose = inCurrent.Shift;
            StatusText = $"已自动移调 {Format(inCurrent.Shift)} 半音（{SelectedInstrument.Name}，可演奏 {inCurrent.Score}/{total}）";
            return;
        }

        // 切换到更合适的乐器
        bool switched = best.Instrument != SelectedInstrument;
        if (switched) SelectedInstrument = best.Instrument;
        Transpose = best.Shift;

        if (switched)
        {
            StatusText =
                $"当前乐器({SelectedInstrument.Name})最佳仅可演奏 {inCurrent.Score}/{total}，" +
                $"已自动切换到 {best.Instrument.Name}，移调 {Format(best.Shift)} 半音 → 可演奏 {best.Score}/{total}";
        }
        else
        {
            StatusText = $"已自动移调 {Format(best.Shift)} 半音（{best.Instrument.Name}，可演奏 {best.Score}/{total}）";
        }

        static string Format(int s) => (s >= 0 ? "+" : "") + s;
    }

    [RelayCommand]
    private void TransposeUp() => Transpose++;

    [RelayCommand]
    private void TransposeDown() => Transpose--;

    [RelayCommand]
    private void Play()
    {
        if (Notes is null || Notes.Count == 0) return;
        if (IsPlaying) return; // 已在播放中，忽略
        IsPlaying = true;
        StatusText = $"从 {FormatTime(Playhead)} 开始演奏（请切换到原神窗口）";
        _player.Speed = Speed;
        _player.Play(Notes.ToList(), Playhead, CountdownSeconds);
    }

    /// <summary>暂停：停止演奏但保留当前播放位置，再次点击播放可从此处继续。</summary>
    [RelayCommand]
    private void Pause()
    {
        if (!IsPlaying && string.IsNullOrEmpty(CountdownText)) return;
        _player.Stop();
        IsPlaying = false;
        CountdownText = string.Empty;
        StatusText = $"已暂停于 {FormatTime(Playhead)}";
    }

    /// <summary>播放/暂停 切换（工具栏按钮 + 全局热键 F8 都走这里）。</summary>
    [RelayCommand]
    public void TogglePlayPause()
    {
        if (IsPlaying) Pause();
        else Play();
    }

    [RelayCommand]
    private void Stop()
    {
        _player.Stop();
        IsPlaying = false;
        CountdownText = string.Empty;
        Playhead = 0;
        StatusText = "已停止";
    }

    [RelayCommand]
    private void RestartFromStart()
    {
        _player.Stop();
        IsPlaying = false;
        CountdownText = string.Empty;
        Playhead = 0;
        Play();
    }

    public void Seek(double seconds)
    {
        bool wasPlaying = IsPlaying;
        if (wasPlaying) _player.Stop();
        Playhead = Math.Clamp(seconds, 0, Math.Max(0, Duration));
        IsPlaying = false;
        CountdownText = string.Empty;
        if (wasPlaying) Play();
    }

    private void RefreshStats()
    {
        int total = Notes?.Count ?? 0;
        int supp = 0;
        if (Notes != null)
            foreach (var n in Notes) if (n.Supported) supp++;
        TotalNotes = total;
        SupportedNotes = supp;
        UnsupportedNotes = total - supp;
    }

    public static string FormatTime(double sec)
    {
        if (double.IsNaN(sec) || sec < 0) sec = 0;
        int total = (int)Math.Round(sec);
        return $"{total / 60:00}:{total % 60:00}";
    }

    public void Shutdown() => _player.Dispose();
}
