using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
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

    /// <summary>
    /// 播放会话计数器：每次主动 <see cref="StopPlayer"/>（Pause / Stop / Seek / 切谱 等）
    /// 都会自增。<see cref="Player.PlayheadChanged"/> 在 Player 线程同步触发时会捕获
    /// 当时的会话号，UI 线程派发 lambda 在跑之前对照当前会话号——不一致即丢弃。
    /// 这样就能避免"上一次播放线程在被取消之前已经 Post 到 UI 队列、但还没轮到执行的
    /// 那批 Playhead 更新"在 Seek 完成后回写陈旧的 Playhead 值，进而触发 View 层
    /// 误以为播放头还在视野外，错误地把视野挪走的问题。
    /// </summary>
    private int _playSession;

    /// <summary>
    /// Player 线程的"自然向前推进"通知，仅在 PlayheadChanged 通过会话校验后触发。
    /// View 层用它来驱动播放过程中的视野自动翻页——不绑到 <see cref="Playhead"/>
    /// 的 PropertyChanged 上是为了把"用户 Seek / Slider 拖动 / 陈旧异步通知"导致的
    /// Playhead 写入跟"播放线自然推进"区分开，前者绝不应该让视野动。
    /// </summary>
    public event Action? PlayerAdvanced;

    // ===== 演奏输出（按 SelectedPlaybackMode 切换）=====
    // - KeyboardOutput 给原神演奏用，无状态、随时可用、不需要 dispose；
    // - MidiPreviewOutput 占用一个 MIDI 设备句柄，按需创建 / 释放。
    private readonly INoteOutput _keyboardOutput = new KeyboardOutput();
    private MidiPreviewOutput? _previewOutput;

    public MainWindowViewModel()
    {
        _player.PlayheadChanged += p =>
        {
            // 在 Player 线程同步捕获当前会话号；旧会话被 StopPlayer 自增过，
            // UI 派发 lambda 跑起来时一比对就能识别出"取消前 Post 了、取消后才轮到执行"
            // 的陈旧通知并直接丢弃。
            int session = Volatile.Read(ref _playSession);
            Dispatcher.UIThread.Post(() =>
            {
                if (session != Volatile.Read(ref _playSession)) return;
                if (IsDraggingTimeline) return;
                Playhead = p;
                if (Duration > 0 && Playhead >= Duration) { /* will finish */ }
                PlayerAdvanced?.Invoke();
            });
        };
        _player.Finished += () => Dispatcher.UIThread.Post(() =>
        {
            IsPlaying = false;
            CountdownText = string.Empty;
        });
        _player.CountdownTick += sec => Dispatcher.UIThread.Post(() =>
        {
            CountdownText = sec > 0 ? $"准备演奏: {sec}" : string.Empty;
        });

        _scheduleTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(500),
            DispatcherPriority.Background,
            (_, _) => CheckSchedule());

        Tracks.CollectionChanged += OnTracksCollectionChanged;

        // 默认选中第一项 = 演奏到原神
        _selectedPlaybackMode = AvailableModes[0];
    }

    /// <summary>
    /// 停止 Player 并自增播放会话号——所有"主动取消上一次演奏"的入口都应该走这里，
    /// 这样旧 Player 线程在被取消之前 <c>Dispatcher.UIThread.Post</c> 出去、还没轮到执行
    /// 的那些 PlayheadChanged 通知会因为会话号不匹配而被忽略，避免它们在 Seek/Stop/Pause
    /// 完成之后把 <see cref="Playhead"/> 重写回旧值（曾经导致 Seek 到视野内时视野被
    /// AutoPage 错误移开）。
    /// </summary>
    private void StopPlayer()
    {
        _player.Stop();
        Interlocked.Increment(ref _playSession);
    }

    // ===== 演奏模式 =====

    /// <summary>所有可选演奏模式（供 ComboBox 绑定）。</summary>
    public IReadOnlyList<PlaybackModeOption> AvailableModes { get; } = new[]
    {
        new PlaybackModeOption
        {
            Mode = PlaybackMode.Genshin,
            Name = "🎮 演奏到原神",
            Description = "按键事件发到当前焦点窗口（请切到游戏）",
        },
        new PlaybackModeOption
        {
            Mode = PlaybackMode.PreviewLyre,
            Name = "🔊 试听·琴键映射",
            Description = "本机播放，与游戏一致（跳过红色不支持音）",
        },
        new PlaybackModeOption
        {
            Mode = PlaybackMode.PreviewOriginal,
            Name = "🎼 试听·完整原曲",
            Description = "本机播放完整原 MIDI（不受琴键 / 移调限制）",
        },
    };

    private PlaybackModeOption _selectedPlaybackMode = null!;

    /// <summary>当前选中的演奏模式选项；构造函数末尾会被初始化为列表第一项。</summary>
    public PlaybackModeOption SelectedPlaybackMode
    {
        get => _selectedPlaybackMode;
        set
        {
            if (_selectedPlaybackMode == value) return;
            var old = _selectedPlaybackMode;
            SetProperty(ref _selectedPlaybackMode, value);
            OnPlaybackModeChanged(old?.Mode ?? PlaybackMode.Genshin, value?.Mode ?? PlaybackMode.Genshin);
        }
    }

    /// <summary>当前模式（投影自 SelectedPlaybackMode，便于其它逻辑判断）。</summary>
    public PlaybackMode CurrentMode => _selectedPlaybackMode?.Mode ?? PlaybackMode.Genshin;

    /// <summary>是否处于"本机试听"模式（任意一种试听）。</summary>
    public bool IsPreviewMode =>
        CurrentMode == PlaybackMode.PreviewLyre || CurrentMode == PlaybackMode.PreviewOriginal;

    /// <summary>
    /// 用户切换演奏模式后的处理：
    /// <list type="bullet">
    ///   <item>正在演奏中的话立即停下，避免上一种输出在新模式里继续发声 / 发按键；</item>
    ///   <item>切回原神模式时释放 MIDI 设备句柄；切到任一试听模式时按需创建并配置音色；</item>
    ///   <item>更新播放按钮文字 / 状态栏提示。</item>
    /// </list>
    /// </summary>
    private void OnPlaybackModeChanged(PlaybackMode oldMode, PlaybackMode newMode)
    {
        if (IsPlaying)
        {
            StopPlayer();
            IsPlaying = false;
            CountdownText = string.Empty;
        }

        if (newMode == PlaybackMode.Genshin)
        {
            // 不需要 MIDI 设备了，及时释放
            _previewOutput?.Dispose();
            _previewOutput = null;
            StatusText = "已切换到「演奏到原神」：按 F8 / 播放后请切到游戏窗口";
        }
        else
        {
            bool useOriginal = newMode == PlaybackMode.PreviewOriginal;
            int program = useOriginal
                ? 0   // 原曲试听：通用钢琴 (Acoustic Grand Piano)
                : GetPreviewProgramForGroup(SelectedInstrumentGroup);

            // 试听口味变了（琴键 ↔ 原曲），需要重建一个新的 MidiPreviewOutput；
            // 同口味下只更新音色即可，省一次设备开闭。
            if (_previewOutput == null || _previewOutput.UseOriginalPitch != useOriginal)
            {
                _previewOutput?.Dispose();
                try
                {
                    _previewOutput = new MidiPreviewOutput(useOriginal, program);
                }
                catch (Exception ex)
                {
                    _previewOutput = null;
                    StatusText = $"试听初始化失败: {ex.Message}";
                    // 退回到原神模式，避免后续 StartPlayback 拿到 null
                    _selectedPlaybackMode = AvailableModes[0];
                    OnPropertyChanged(nameof(SelectedPlaybackMode));
                    OnPropertyChanged(nameof(CurrentMode));
                    OnPropertyChanged(nameof(IsPreviewMode));
                    OnPropertyChanged(nameof(PlayPauseButtonText));
                    OnPropertyChanged(nameof(PlayPauseButtonTooltip));
                    return;
                }
            }
            else
            {
                _previewOutput.SetProgram(program);
            }

            StatusText = useOriginal
                ? "已切换到「试听·完整原曲」：声音从本机扬声器播放"
                : "已切换到「试听·琴键映射」：声音从本机扬声器播放（与游戏一致）";
        }

        OnPropertyChanged(nameof(CurrentMode));
        OnPropertyChanged(nameof(IsPreviewMode));
        OnPropertyChanged(nameof(PlayPauseButtonText));
        OnPropertyChanged(nameof(PlayPauseButtonTooltip));
    }

    /// <summary>
    /// 不同琴类乐器组在"琴键试听"下用什么 GM 音色更接近原曲——竖琴对应风物之诗琴 / 老旧的诗琴
    /// 这种弦乐器，圆号则用 GM French Horn。原曲试听不走这里。
    /// </summary>
    private static int GetPreviewProgramForGroup(InstrumentGroup g) => g.Id switch
    {
        "windsong-horn" => 60, // French Horn
        _ => 46,               // Orchestral Harp
    };

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

            // 试听模式期望"实时混音"：Mute/Solo 一变就用新的可发声集合从当前
            // Playhead 重启 Player（Player 内部一开始就把 audible 轨道一次性快照
            // 到自己的 upcoming 列表里，否则演奏中改 Mute/Solo 是看不到效果的）。
            // 原神模式不自动重启，避免抖动时反复给游戏送扫描码。
            if (IsPlaying && IsPreviewMode)
            {
                LiveRestartFromPlayhead();
            }
        }
        else if (e.PropertyName == nameof(MidiTrack.OctaveOffset) && sender is MidiTrack tr)
        {
            // 单轨八度偏移变化：只重新计算这一条轨道的 EffectivePitch / Key / Supported，
            // 然后刷新统计 + 试听重启（与全局 Transpose 变化的处理保持一致，但不波及其它轨道）。
            ReapplyMappingForTrack(tr);
            RefreshStats();
            if (IsPlaying && IsPreviewMode)
            {
                LiveRestartFromPlayhead();
            }
        }
    }

    /// <summary>
    /// 试听模式下因可发声集合 / 音符内容发生变化时，从当前 <see cref="Playhead"/>
    /// 立即重启 Player；改完后没有任何可演奏轨道的话会停在当前位置，
    /// <see cref="StartPlayback"/> 会写好状态栏提示。
    /// </summary>
    private void LiveRestartFromPlayhead()
    {
        StopPlayer();
        IsPlaying = false;
        CountdownText = string.Empty;
        StartPlayback(0);
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

    /// <summary>
    /// 用户正在拖动时间线滑块时设为 true，阻止 PlayheadChanged 回写 Playhead，
    /// 避免滑块在播放过程中被 30Hz 的进度更新反复拉回当前位置而无法拖动。
    /// </summary>
    public bool IsDraggingTimeline { get; set; }
    [ObservableProperty] private int _transpose;
    [ObservableProperty] private double _pixelsPerSecond = 120;
    [ObservableProperty] private int _countdownSeconds = 3;
    [ObservableProperty] private double _speed = 1.0;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string? _countdownText;
    [ObservableProperty] private string? _statusText = "就绪";

    // ===== 定时演奏 =====

    /// <summary>定时演奏的目标时间（北京时间 UTC+8，仅取时分）。</summary>
    [ObservableProperty] private TimeSpan _scheduledTime = new(12, 0, 0);

    /// <summary>是否启用定时演奏。</summary>
    [ObservableProperty] private bool _isScheduled;

    /// <summary>定时演奏状态文本，显示在工具栏上。</summary>
    [ObservableProperty] private string? _scheduledStatus;

    private DispatcherTimer _scheduleTimer = null!;
    private DateTime _lastTriggeredDate;

    partial void OnIsScheduledChanged(bool value)
    {
        if (value)
        {
            var now = BeijingNow;
            var target = now.Date + ScheduledTime;
            // 如果今天的目标时间已过，标记今天已触发，等到明天
            _lastTriggeredDate = now >= target ? now.Date : default;
            _scheduleTimer.Start();
            // 立即刷新一次倒计时
            CheckSchedule();
        }
        else
        {
            _scheduleTimer.Stop();
            ScheduledStatus = null;
        }
    }

    private static DateTime BeijingNow => DateTime.UtcNow.AddHours(8);

    private void CheckSchedule()
    {
        if (!IsScheduled) return;

        var now = BeijingNow;

        if (_lastTriggeredDate.Date == now.Date) return;

        var target = now.Date + ScheduledTime;
        if (now >= target)
        {
            _lastTriggeredDate = now.Date;
            if (Tracks.Count == 0)
            {
                ScheduledStatus = "未加载曲谱，无法定时演奏";
                IsScheduled = false;
                return;
            }
            if (IsPlaying)
            {
                ScheduledStatus = "正在演奏中";
                IsScheduled = false;
                return;
            }
            ScheduledStatus = null;
            IsScheduled = false;
            Play();
        }
        else
        {
            var remain = target - now;
            ScheduledStatus = $"将在 {ScheduledTime:hh\\:mm} 开始（{remain.Hours:D2}:{remain.Minutes:D2}:{remain.Seconds:D2}）";
        }
    }

    public IReadOnlyList<InstrumentGroup> AvailableInstrumentGroups { get; } = Instruments.Groups;

    [ObservableProperty] private InstrumentGroup _selectedInstrumentGroup = Instruments.Default;

    public string PlayPauseButtonText
    {
        get
        {
            if (IsPlaying) return "⏸ 暂停 (F8)";
            return CurrentMode switch
            {
                PlaybackMode.PreviewLyre => "▶ 试听 (F8)",
                PlaybackMode.PreviewOriginal => "▶ 试听原曲 (F8)",
                _ => "▶ 播放 (F8)",
            };
        }
    }

    public string PlayPauseButtonTooltip
    {
        get
        {
            if (IsPlaying)
                return "暂停演奏，保留当前播放位置，再次按可继续(全局热键 F8)";
            return CurrentMode switch
            {
                PlaybackMode.PreviewLyre =>
                    "在本程序内试听（按琴键映射）：声音从扬声器播放，不会向游戏发送按键。也可使用全局热键 F8。",
                PlaybackMode.PreviewOriginal =>
                    "在本程序内试听（完整原曲）：按 MIDI 原音高完整播放，不受琴键限制 / 移调。也可使用全局热键 F8。",
                _ =>
                    "开始 / 继续演奏。也可使用全局热键 F8（焦点在原神里也生效）",
            };
        }
    }

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
        // 琴键试听模式下，切换乐器组也要把试听音色跟着切（竖琴 / 圆号 …）；
        // 原曲试听用固定钢琴音色，跟乐器组无关。
        if (_previewOutput != null && !_previewOutput.UseOriginalPitch && value != null)
        {
            _previewOutput.SetProgram(GetPreviewProgramForGroup(value));
        }
        if (value != null)
            StatusText = $"已切换到 {value.Name}（{value.Description}）";
    }

    /// <summary>
    /// 基于当前 Transpose + SelectedInstrumentGroup 重算所有轨道的 Key / Supported。
    /// 每条轨道在全局 Transpose 之外还会叠加自己的 <see cref="MidiTrack.OctaveOffset"/>（×12 半音）。
    /// </summary>
    private void ReapplyMapping()
    {
        if (Tracks.Count == 0) return;
        foreach (var tr in Tracks)
        {
            MidiParser.ApplyTranspose(tr.Notes, Transpose + tr.OctaveOffset * 12, SelectedInstrumentGroup);
            // 重新赋值触发 Notes 变更事件，让绑定的控件（缩略图/钢琴卷帘）重绘。
            tr.Notes = new List<Note>(tr.Notes);
        }
        if (SelectedTrack != null) Notes = SelectedTrack.Notes;
        RefreshStats();
    }

    /// <summary>仅对单条轨道重算映射，用于 OctaveOffset 变化等单轨事件。</summary>
    private void ReapplyMappingForTrack(MidiTrack tr)
    {
        MidiParser.ApplyTranspose(tr.Notes, Transpose + tr.OctaveOffset * 12, SelectedInstrumentGroup);
        tr.Notes = new List<Note>(tr.Notes);
        if (SelectedTrack == tr) Notes = tr.Notes;
    }

    partial void OnSpeedChanged(double value) => _player.Speed = value;

    /// <summary>
    /// 从字母谱文本加载乐谱。流程与 <see cref="LoadMidiAsync"/> 基本一致，
    /// 但用 <see cref="LetterScoreParser"/> 替代 <see cref="MidiParser"/>。
    /// </summary>
    public void LoadFromLetterScore(string text)
    {
        try
        {
            // 切换曲谱前先停止演奏并清理上一次的曲谱引用
            StopPlayer();
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

            StatusText = "正在解析字母谱…";
            var result = LetterScoreParser.Parse(text);
            FilePath = null;
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
            UpdateAudibleStates();
            SelectedTrack = Tracks.FirstOrDefault();

            if (Transpose != 0)
                Transpose = 0;
            else
                ReapplyMapping();

            StatusText = $"已导入字母谱：{Tracks.Count} 条轨道 / {TotalNotes} 个音符，时长 {FormatTime(Duration)}";
        }
        catch (Exception ex)
        {
            StatusText = $"字母谱导入失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 将当前已加载的轨道导出为 MIDI 文件。
    /// </summary>
    public void ExportMidi(string path)
    {
        try
        {
            if (Tracks.Count == 0)
            {
                StatusText = "没有可导出的轨道";
                return;
            }
            MidiExporter.Export(path, Tracks, _tempoManager, _timeSignatureManager);
            StatusText = $"已导出 MIDI 文件: {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusText = $"导出 MIDI 失败: {ex.Message}";
        }
    }

    public async Task LoadMidiAsync(string path)
    {
        try
        {
            // 切换曲谱前先停止演奏并清理上一次的曲谱引用
            StopPlayer();
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
        // 已应用各轨 OctaveOffset：自动移调时认为本轨用户已经做了八度微调，
        // 在此基础上再寻找最优全局 Transpose，不会推翻用户的单轨决策。
        var activePitches = new List<int>();
        foreach (var t in Tracks)
        {
            if (!t.IsAudible) continue;
            int oct12 = t.OctaveOffset * 12;
            foreach (var n in t.Notes) activePitches.Add(n.OriginalPitch + oct12);
        }
        int total = activePitches.Count;
        if (total == 0)
        {
            StatusText = "当前没有可演奏的轨道（请检查 Mute / Solo 状态）";
            return;
        }

        var inCurrent = MidiParser.FindBestTransposeWithScore(activePitches, SelectedInstrumentGroup);

        if (inCurrent.Score >= total)
        {
            Transpose = inCurrent.Shift;
            StatusText = $"已自动移调 {Format(inCurrent.Shift)} 半音（{SelectedInstrumentGroup.Name}，全部 {total} 音均可演奏）";
            return;
        }

        var best = MidiParser.FindBestTransposeAcrossGroups(activePitches, AvailableInstrumentGroups, SelectedInstrumentGroup);

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

    /// <summary>单轨 OctaveOffset 的合理范围，避免把音符推到 MIDI 0–127 之外或离谱的 ±10 八度。</summary>
    public const int MinTrackOctaveOffset = -4;
    public const int MaxTrackOctaveOffset = 4;

    /// <summary>把指定轨道的八度偏移 +1（带上限钳制）。</summary>
    [RelayCommand]
    private void TrackOctaveUp(MidiTrack? track)
    {
        if (track == null) return;
        if (track.OctaveOffset >= MaxTrackOctaveOffset) return;
        track.OctaveOffset++;
    }

    /// <summary>把指定轨道的八度偏移 −1（带下限钳制）。</summary>
    [RelayCommand]
    private void TrackOctaveDown(MidiTrack? track)
    {
        if (track == null) return;
        if (track.OctaveOffset <= MinTrackOctaveOffset) return;
        track.OctaveOffset--;
    }

    [RelayCommand]
    private void Play()
    {
        if (IsPlaying) return;
        StartPlayback(CountdownSeconds);
    }

    /// <summary>
    /// 从当前 <see cref="Playhead"/> 启动 Player。<paramref name="countdownSeconds"/>
    /// 为 0 时跳过准备倒计时（用于 Seek 等"已经在播放中"的场景）。
    /// 试听模式下也会跳过倒计时——本机试听不需要切到游戏。
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

        // 选具体输出：原神模式 → 键盘注入；任一试听模式 → 本机 MIDI 合成器
        INoteOutput output;
        if (CurrentMode == PlaybackMode.Genshin)
        {
            output = _keyboardOutput;
        }
        else
        {
            // OnPlaybackModeChanged 里应该已经创建好 _previewOutput；理论上不会为 null，
            // 兜底再创建一次（例如设备初始化失败后用户改了乐器又切回来的边角情况）。
            if (_previewOutput == null)
            {
                bool useOriginal = CurrentMode == PlaybackMode.PreviewOriginal;
                int program = useOriginal ? 0 : GetPreviewProgramForGroup(SelectedInstrumentGroup);
                try
                {
                    _previewOutput = new MidiPreviewOutput(useOriginal, program);
                }
                catch (Exception ex)
                {
                    StatusText = $"试听初始化失败: {ex.Message}";
                    return;
                }
            }
            output = _previewOutput;
            // 试听不需要倒计时，0 = 立即开始
            countdownSeconds = 0;
        }

        IsPlaying = true;
        StatusText = CurrentMode switch
        {
            PlaybackMode.Genshin =>
                countdownSeconds > 0
                    ? $"从 {FormatTime(Playhead)} 开始演奏（请切换到原神窗口）"
                    : $"从 {FormatTime(Playhead)} 继续演奏",
            PlaybackMode.PreviewLyre =>
                $"试听·琴键映射 · 从 {FormatTime(Playhead)} 开始（声音在本机播放）",
            PlaybackMode.PreviewOriginal =>
                $"试听·完整原曲 · 从 {FormatTime(Playhead)} 开始（声音在本机播放）",
            _ => $"从 {FormatTime(Playhead)} 开始",
        };
        _player.Speed = Speed;
        _player.Play(combined, Playhead, countdownSeconds, output);
    }

    [RelayCommand]
    private void Pause()
    {
        if (!IsPlaying && string.IsNullOrEmpty(CountdownText)) return;
        StopPlayer();
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
        StopPlayer();
        IsPlaying = false;
        CountdownText = string.Empty;
        Playhead = 0;
        StatusText = "已停止";
    }

    [RelayCommand]
    private void RestartFromStart()
    {
        StopPlayer();
        IsPlaying = false;
        CountdownText = string.Empty;
        Playhead = 0;
        Play();
    }

    public void Seek(double seconds)
    {
        bool wasPlaying = IsPlaying;
        if (wasPlaying) StopPlayer();
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
            StopPlayer();
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
        _previewOutput?.Dispose();
        _previewOutput = null;
    }
}
