using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
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

        Tracks.CollectionChanged += OnTracksCollectionChanged;
    }

    // ===== 多轨 =====

    /// <summary>所有已加载的轨道（解析后填充）。</summary>
    public ObservableCollection<MidiTrack> Tracks { get; } = new();

    /// <summary>当前在钢琴卷帘中显示的轨道。</summary>
    [ObservableProperty] private MidiTrack? _selectedTrack;

    /// <summary>钢琴卷帘的可见时间范围，由 View 层根据 ScrollViewer 更新。</summary>
    [ObservableProperty] private double _viewportStart;
    [ObservableProperty] private double _viewportEnd;

    partial void OnSelectedTrackChanged(MidiTrack? value)
    {
        Notes = value?.Notes;
    }

    private void OnTracksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (MidiTrack t in e.OldItems) t.PropertyChanged -= OnTrackItemChanged;
        if (e.NewItems != null)
            foreach (MidiTrack t in e.NewItems) t.PropertyChanged += OnTrackItemChanged;
    }

    private void OnTrackItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Mute 变化不影响展示，只在下次 Play 时生效。这里无需额外处理。
    }

    // ===== 基本状态 =====

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

    public IReadOnlyList<InstrumentGroup> AvailableInstrumentGroups { get; } = Instruments.Groups;

    [ObservableProperty] private InstrumentGroup _selectedInstrumentGroup = Instruments.Default;

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

    partial void OnTransposeChanged(int value) => ReapplyMapping();

    partial void OnSelectedInstrumentGroupChanged(InstrumentGroup value)
    {
        ReapplyMapping();
        if (value != null)
            StatusText = $"已切换到 {value.Name}（{value.Description}）";
    }

    /// <summary>基于当前 Transpose + SelectedInstrumentGroup 重算所有轨道的 Key / Supported。</summary>
    private void ReapplyMapping()
    {
        if (Tracks.Count == 0) return;
        foreach (var tr in Tracks)
        {
            MidiParser.ApplyTranspose(tr.Notes, Transpose, SelectedInstrumentGroup);
            // 重新赋值触发 Notes 变更事件，让绑定的控件（缩略图/钢琴卷帘）重绘。
            tr.Notes = new List<Note>(tr.Notes);
        }
        if (SelectedTrack != null) Notes = SelectedTrack.Notes;
        RefreshStats();
    }

    partial void OnSpeedChanged(double value) => _player.Speed = value;

    public async Task LoadMidiAsync(string path)
    {
        try
        {
            // 切换曲谱前先停止演奏并清理上一次的曲谱引用
            _player.Stop();
            IsPlaying = false;
            CountdownText = string.Empty;
            Notes = null;
            SelectedTrack = null;
            Tracks.Clear();
            TotalNotes = 0;
            SupportedNotes = 0;
            UnsupportedNotes = 0;
            Playhead = 0;
            Duration = 0;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            StatusText = "正在解析…";
            var result = await Task.Run(() => MidiParser.Parse(path));
            FilePath = path;
            FileName = result.FileName;
            Duration = result.TotalDuration;
            Playhead = 0;

            foreach (var tr in result.Tracks) Tracks.Add(tr);

            // 先选第一个轨道，再按需触发移调重算
            SelectedTrack = Tracks.FirstOrDefault();

            if (Transpose != 0)
                Transpose = 0; // 触发 OnTransposeChanged → ReapplyMapping
            else
                ReapplyMapping();

            StatusText = $"已加载 {Tracks.Count} 条轨道 / {TotalNotes} 个音符，时长 {FormatTime(Duration)}";
        }
        catch (Exception ex)
        {
            StatusText = $"加载失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private void AutoTranspose()
    {
        if (Tracks.Count == 0) return;
        // 仅对当前未静音的轨道做评估（静音轨不会演奏，不需要考虑）。
        var activeNotes = Tracks.Where(t => !t.Muted).SelectMany(t => t.Notes).ToList();
        if (activeNotes.Count == 0) activeNotes = Tracks.SelectMany(t => t.Notes).ToList();
        int total = activeNotes.Count;
        if (total == 0) return;

        var inCurrent = MidiParser.FindBestTransposeWithScore(activeNotes, SelectedInstrumentGroup);

        if (inCurrent.Score >= total)
        {
            Transpose = inCurrent.Shift;
            StatusText = $"已自动移调 {Format(inCurrent.Shift)} 半音（{SelectedInstrumentGroup.Name}，全部 {total} 音均可演奏）";
            return;
        }

        var best = MidiParser.FindBestTransposeAcrossGroups(activeNotes, AvailableInstrumentGroups, SelectedInstrumentGroup);

        if (best.Score <= inCurrent.Score)
        {
            Transpose = inCurrent.Shift;
            StatusText = $"已自动移调 {Format(inCurrent.Shift)} 半音（{SelectedInstrumentGroup.Name}，可演奏 {inCurrent.Score}/{total}）";
            return;
        }

        bool switched = best.Group != SelectedInstrumentGroup;
        if (switched) SelectedInstrumentGroup = best.Group;
        Transpose = best.Shift;

        if (switched)
        {
            StatusText =
                $"当前乐器组({SelectedInstrumentGroup.Name})最佳仅可演奏 {inCurrent.Score}/{total}，" +
                $"已自动切换到 {best.Group.Name}，移调 {Format(best.Shift)} 半音 → 可演奏 {best.Score}/{total}";
        }
        else
        {
            StatusText = $"已自动移调 {Format(best.Shift)} 半音（{best.Group.Name}，可演奏 {best.Score}/{total}）";
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
        if (Tracks.Count == 0) return;
        if (IsPlaying) return;

        // 演奏时把所有未静音轨道的音符合并后按时间排序。
        var combined = Tracks.Where(t => !t.Muted).SelectMany(t => t.Notes).ToList();
        combined.Sort((a, b) => a.Start.CompareTo(b.Start));
        if (combined.Count == 0)
        {
            StatusText = "所有轨道都已静音，无可演奏内容";
            return;
        }

        IsPlaying = true;
        StatusText = $"从 {FormatTime(Playhead)} 开始演奏（请切换到原神窗口）";
        _player.Speed = Speed;
        _player.Play(combined, Playhead, CountdownSeconds);
    }

    [RelayCommand]
    private void Pause()
    {
        if (!IsPlaying && string.IsNullOrEmpty(CountdownText)) return;
        _player.Stop();
        IsPlaying = false;
        CountdownText = string.Empty;
        StatusText = $"已暂停于 {FormatTime(Playhead)}";
    }

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
        int total = 0, supp = 0;
        foreach (var tr in Tracks)
        {
            foreach (var n in tr.Notes)
            {
                total++;
                if (n.Supported) supp++;
            }
        }
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
