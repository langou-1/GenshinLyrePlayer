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
        // Mute / Solo 变化不影响显示，但会改变"哪些轨道实际会被演奏"，
        // 进而影响自动移调评估范围、底部音符统计数字、以及缩略图上的明暗。
        // 这里把全局判定结果（IsAudible）一次性刷新到所有轨道，再触发统计。
        if (e.PropertyName == nameof(MidiTrack.Muted) ||
            e.PropertyName == nameof(MidiTrack.Solo))
        {
            UpdateAudibleStates();
            RefreshStats();
        }
    }

    /// <summary>
    /// 综合 Mute / Solo 状态，按 TuneLab 的规则把每条轨道的最终"是否实际演奏"写入
    /// <see cref="MidiTrack.IsAudible"/>：
    /// <list type="bullet">
    ///   <item>只要存在任意一条 Solo=true 的轨道（hasSolo），未 Solo 的轨道就全部静音；</item>
    ///   <item>没有 Solo 时，普通的 Muted 决定是否静音；</item>
    ///   <item>同一条轨道上 Solo 优先于 Muted（Solo=true 即可演奏）。</item>
    /// </list>
    /// </summary>
    private void UpdateAudibleStates()
    {
        bool hasSolo = false;
        foreach (var tr in Tracks) { if (tr.Solo) { hasSolo = true; break; } }

        foreach (var tr in Tracks)
        {
            bool audible = tr.Solo || (!tr.Muted && !hasSolo);
            if (tr.IsAudible != audible) tr.IsAudible = audible;
        }
    }

    // ===== 基本状态 =====

    [ObservableProperty] private IReadOnlyList<Note>? _notes;
    /// <summary>
    /// 整首曲子里所有曲速变化标签（用于速度轨）。
    /// 实际数据由 <see cref="_tempoManager"/> 持有并维护，本字段只是其 Markers 的快照引用，
    /// 在 BPM 被编辑后会被重新赋值（new List 包一层）以触发绑定的 TempoStrip 重绘。
    /// </summary>
    [ObservableProperty] private IReadOnlyList<TempoMarker> _tempos = Array.Empty<TempoMarker>();

    /// <summary>当前曲子的曲速管理器；切换 MIDI 时整体替换。</summary>
    private TempoManager? _tempoManager;

    /// <summary>
    /// 当前曲子的曲速管理器，对外暴露给 PianoRoll —— 钢琴卷帘按 tick 绘格线时
    /// 用它把 bar/beat 的 tick 边界折算成秒。BPM 编辑后实例不变，但内部
    /// markers 的 Time 会被原地改写；为了让 PianoRoll 重绘，我们在
    /// <see cref="OnTempoManagerChanged"/> 里把 Notes 引用换新触发重绘。
    /// </summary>
    public TempoManager? TempoManagerForView => _tempoManager;

    /// <summary>
    /// 当前曲子的拍号管理器，对外暴露给 PianoRoll；切换 MIDI 时整体替换，运行期不变。
    /// 名字加 "ForView" 后缀，避免与类型 <see cref="TimeSignatureManager"/> 同名导致的编译歧义。
    /// </summary>
    private TimeSignatureManager? _timeSignatureManager;
    public TimeSignatureManager? TimeSignatureManagerForView
    {
        get => _timeSignatureManager;
        private set => SetProperty(ref _timeSignatureManager, value);
    }

    /// <summary>所有轨道里最大的 NoteEnd 的 tick 位置，用于在 BPM 变化后重算总时长。</summary>
    private long _maxEndTick;

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
            Tempos = Array.Empty<TempoMarker>();
            if (_tempoManager != null)
            {
                _tempoManager.Changed -= OnTempoManagerChanged;
                _tempoManager = null;
                OnPropertyChanged(nameof(TempoManagerForView));
            }
            TimeSignatureManagerForView = null;
            _maxEndTick = 0;
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
            _tempoManager = result.TempoManager;
            _tempoManager.Changed += OnTempoManagerChanged;
            OnPropertyChanged(nameof(TempoManagerForView));
            TimeSignatureManagerForView = result.TimeSignatureManager;
            _maxEndTick = result.MaxEndTick;
            Duration = result.TotalDuration;
            Playhead = 0;
            Tempos = new List<TempoMarker>(_tempoManager.Markers);

            foreach (var tr in result.Tracks) Tracks.Add(tr);

            // 新加载的轨道默认 Mute/Solo 都是 false，IsAudible 默认 true，
            // 这里仍统一刷新一次以防文件解析阶段意外携带状态。
            UpdateAudibleStates();

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
        // 仅对当前实际会演奏的轨道做评估（被 Mute 或被其它 Solo 屏蔽的轨道不参与）。
        var activeNotes = Tracks.Where(t => t.IsAudible).SelectMany(t => t.Notes).ToList();
        int total = activeNotes.Count;
        if (total == 0)
        {
            StatusText = "当前没有可演奏的轨道（请检查 Mute / Solo 状态）";
            return;
        }

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
        if (IsPlaying) return;
        StartPlayback(CountdownSeconds);
    }

    /// <summary>
    /// 从当前 <see cref="Playhead"/> 启动 Player。<paramref name="countdownSeconds"/>
    /// 为 0 时跳过准备倒计时（用于 Seek 等"已经在播放中"的场景）。
    /// </summary>
    private void StartPlayback(int countdownSeconds)
    {
        if (Tracks.Count == 0) return;

        // 演奏时把所有"实际可发声"的轨道（未被 Mute、且未被其它 Solo 屏蔽）合并后按时间排序。
        var combined = Tracks.Where(t => t.IsAudible).SelectMany(t => t.Notes).ToList();
        combined.Sort((a, b) => a.Start.CompareTo(b.Start));
        if (combined.Count == 0)
        {
            StatusText = "当前没有可演奏的轨道（请检查 Mute / Solo 状态）";
            return;
        }

        IsPlaying = true;
        StatusText = countdownSeconds > 0
            ? $"从 {FormatTime(Playhead)} 开始演奏（请切换到原神窗口）"
            : $"从 {FormatTime(Playhead)} 继续演奏";
        _player.Speed = Speed;
        _player.Play(combined, Playhead, countdownSeconds);
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
        CountdownText = string.Empty;
        if (wasPlaying)
        {
            // 播放过程中 Seek：直接从新位置继续播放，跳过准备倒计时。
            StartPlayback(0);
        }
        else
        {
            IsPlaying = false;
        }
    }

    private void RefreshStats()
    {
        // 不会被演奏的轨道（Mute 中、或被其它 Solo 屏蔽）不计入底部"总音符数 / 可演奏音符数"。
        int total = 0, supp = 0;
        foreach (var tr in Tracks)
        {
            if (!tr.IsAudible) continue;
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

    // ===== 曲速编辑 =====

    /// <summary>
    /// View 层把用户在速度轨上敲出的新 BPM 转交给 ViewModel：
    /// 修改前先暂停演奏（避免 Player 线程读到一半被改的 Notes），然后让 TempoManager 写入新值，
    /// 它的 Changed 事件会回到 <see cref="OnTempoManagerChanged"/> 重算所有 Note 的秒时间。
    /// </summary>
    public void RequestBpmChange(TempoMarker marker, double newBpm)
    {
        if (_tempoManager == null) return;
        bool wasPlaying = IsPlaying;
        if (wasPlaying)
        {
            _player.Stop();
            IsPlaying = false;
            CountdownText = string.Empty;
        }
        _tempoManager.SetBpm(marker, newBpm);
        // 这里故意不自动恢复播放：BPM 变化后用户可能想确认下结果，再手动按 F8 继续。
    }

    /// <summary>TempoManager 的 BPM 改写后的统一刷新流水：保持 tick 不变 → 重新折算秒。</summary>
    private void OnTempoManagerChanged()
    {
        if (_tempoManager == null) return;

        // 保持播放头/视野的"音乐位置"（tick）不变，编辑前后 Playhead/视野显示不会跳。
        long playheadTick = _tempoManager.SecondsToTick(Playhead);
        long viewportStartTick = _tempoManager.SecondsToTick(ViewportStart);
        long viewportEndTick = _tempoManager.SecondsToTick(ViewportEnd);

        MidiParser.RecomputeNoteTimes(Tracks, _tempoManager);

        // 重算总时长
        Duration = _tempoManager.TickToSeconds(_maxEndTick);

        // tick → 秒 同步回播放头/视野
        Playhead = _tempoManager.TickToSeconds(playheadTick);
        ViewportStart = _tempoManager.TickToSeconds(viewportStartTick);
        ViewportEnd = _tempoManager.TickToSeconds(viewportEndTick);

        // 触发 Notes 引用变更，让钢琴卷帘 / 缩略图重绘（音符的 Start/Duration 已经原地刷新）
        foreach (var tr in Tracks)
            tr.Notes = new List<Note>(tr.Notes);
        if (SelectedTrack != null)
            Notes = SelectedTrack.Notes;

        // 触发 TempoStrip 重绘（Markers 的 Time 也都被重算了）
        Tempos = new List<TempoMarker>(_tempoManager.Markers);

        StatusText = $"已修改曲速 → 总时长 {FormatTime(Duration)}";
    }

    public void Shutdown()
    {
        if (_tempoManager != null)
        {
            _tempoManager.Changed -= OnTempoManagerChanged;
            _tempoManager = null;
        }
        _player.Dispose();
    }
}
