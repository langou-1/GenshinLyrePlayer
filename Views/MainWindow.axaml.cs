using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GenshinLyrePlayer.Controls;
using GenshinLyrePlayer.Models;
using GenshinLyrePlayer.Services;
using GenshinLyrePlayer.ViewModels;

namespace GenshinLyrePlayer.Views;

public partial class MainWindow : Window
{
    // 全局热键虚拟键码
    private const uint VK_F8 = 0x77;
    private const uint VK_F9 = 0x78;

    public MainWindow()
    {
        InitializeComponent();
        Roll.SeekRequested += OnRollSeek;
        RollScroll.ScrollChanged += (_, _) => UpdateViewport();
        RollScroll.SizeChanged += (_, _) => UpdateViewport();
        TempoStripCtl.BpmChangeRequested += OnTempoBpmChangeRequested;
        DataContextChanged += (_, _) => HookVm();
        KeyDown += OnHotKey;

        // Slider 的 Thumb 在拖动时会吞掉 PointerPressed/PointerReleased 事件
        // （标记为已处理）。这里用 handledEventsToo 强行接收，配合
        // IsDraggingTimeline 标志位阻断 PlayheadChanged → Playhead →
        // Slider.Value 的 30Hz 更新链，避免播放过程中拖动进度条时被弹回。
        TimelineSlider.AddHandler(InputElement.PointerPressedEvent,
            (_, _) => { if (Vm is not null) Vm.IsDraggingTimeline = true; },
            handledEventsToo: true);
        TimelineSlider.AddHandler(InputElement.PointerReleasedEvent,
            OnTimelineDragFinished, handledEventsToo: true);
        TimelineSlider.AddHandler(InputElement.PointerCaptureLostEvent,
            (_, _) => { if (Vm is not null) Vm.IsDraggingTimeline = false; },
            handledEventsToo: true);

        // 拖拽 MIDI 文件到窗口任意位置即可导入
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // 安装全局键盘钩子：即使焦点不在本窗口（例如在原神里）也能响应 F8/F9
        Opened += (_, _) =>
        {
            GlobalKeyHook.KeyDown += OnGlobalKey;
            GlobalKeyHook.Install();
            UpdateViewport();

            // 我们以管理员身份运行（见 app.manifest 中的 requireAdministrator），
            // Windows UIPI 默认会拦截资源管理器（普通完整性级别）发往本窗口的拖放消息，
            // 导致鼠标显示"禁止"图标，DragOver 事件压根不会被触发。
            // 这里在窗口创建后把拖放相关的几条 Windows 消息显式加入白名单。
            var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            UipiDragDropFilter.Allow(handle);
        };
        Closed += (_, _) =>
        {
            GlobalKeyHook.KeyDown -= OnGlobalKey;
            GlobalKeyHook.Uninstall();
        };
    }

    private void OnGlobalKey(uint vk)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Vm is null) return;
            switch (vk)
            {
                case VK_F8:
                    Vm.TogglePlayPause();
                    break;
                case VK_F9:
                    Vm.StopCommand.Execute(null);
                    break;
            }
        });
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private void HookVm()
    {
        if (Vm is null) return;
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.Duration)
             || e.PropertyName == nameof(MainWindowViewModel.PixelsPerSecond))
                Dispatcher.UIThread.Post(UpdateViewport);
            else if (e.PropertyName == nameof(MainWindowViewModel.Playhead))
            {
                // 用户拖动滑块时不更新 Slider.Value，避免 PlayheadChanged
                // 的 30Hz 更新把滑块反复拉回当前播放位置。
                if (!Vm.IsDraggingTimeline)
                    TimelineSlider.Value = Vm.Playhead;
            }
        };
        // 仅在 Player 自然推进时尝试自动翻页——这样用户 Seek（钢琴卷帘点击 / 时间轴滑块
        // 拖放 / Home 键）和 Slider 双向绑定的中间值都不会再触发视野位移。这些入口在调用
        // Vm.Seek 之后会显式调用 EnsurePlayheadVisible 自己决定是否要把 Playhead 拉回视野。
        Vm.PlayerAdvanced += () => Dispatcher.UIThread.Post(AutoPageWhilePlaying);
        UpdateViewport();
    }

    /// <summary>把 ScrollViewer 当前的可见时间区间同步到 VM，驱动缩略图上视野框的绘制。</summary>
    private void UpdateViewport()
    {
        if (Vm is null) return;
        double pps = Math.Max(0.001, Roll.PixelsPerSecond);
        double start = RollScroll.Offset.X / pps;
        double viewWSec = RollScroll.Viewport.Width / pps;
        double end = start + viewWSec;

        // 若钢琴卷帘总宽大于实际可展示区域，末端可能超过 Duration；这里不做 clamp，
        // 让缩略图自己 clamp，保持与实际滚动位置一致。
        Vm.ViewportStart = start;
        Vm.ViewportEnd = end;
    }

    /// <summary>
    /// 播放过程中由 Player 自然推进 Playhead 触发的"自动翻页"：
    /// 当 Playhead 进入视野右侧 25% 区域时把视野往前翻一页，让接下来要演奏的内容露出来。
    /// 仅在 <see cref="MainWindowViewModel.IsPlaying"/> 为真时生效——这样所有由用户
    /// Seek（点击钢琴卷帘 / 拖动时间轴 / Home 键 / Slider 双向绑定）引起的 Playhead 变化
    /// 都不会让视野自动移动；用户 Seek 时是否需要把视野挪到能看见 Playhead 的位置，
    /// 由各 Seek 入口在调用 <see cref="MainWindowViewModel.Seek"/> 之后显式调用
    /// <see cref="EnsurePlayheadVisible"/> 来决定。
    /// 这样分离的好处是：当用户暂停下 Seek 到视野内的位置时，本方法不会启动；
    /// 各种迟到的 Playhead 通知（旧 Player 线程的最后一次 emit、Slider 双向绑定中间值等）
    /// 都因为 IsPlaying 检查或者根本没走这条路而被天然过滤掉。
    /// </summary>
    private void AutoPageWhilePlaying()
    {
        if (Vm is null || !Vm.IsPlaying) return;
        double x = Vm.Playhead * Roll.PixelsPerSecond;
        double viewW = RollScroll.Viewport.Width;
        double offX = RollScroll.Offset.X;
        if (x > offX + viewW * 0.75)
            RollScroll.Offset = new Vector(Math.Max(0, x - viewW * 0.25), RollScroll.Offset.Y);
    }

    /// <summary>
    /// 用户主动 Seek 后调用：仅在 Playhead 跑到当前视野之外时，把视野挪到刚好能看见 Playhead
    /// 的最近一侧；如果 Seek 目标已经在视野内则视野完全不动。这是参考 TuneLab 的"非播放
    /// 状态下只有 Seek 出框才移动视野"的语义实现。
    /// </summary>
    /// <remarks>
    /// 重要：参数 <paramref name="offX"/> / <paramref name="viewW"/> 必须由调用方在
    /// "改动 Playhead 之前"从 <see cref="RollScroll"/> 抓拍后传入，不能在本方法里临时
    /// 读取 <c>RollScroll.Offset.X</c>。原因：在 PianoRoll 的 PointerPressed 处理路径中
    /// 先发出 SeekRequested，进入本类后会同步给 <see cref="MainWindowViewModel.Seek"/> 写一次
    /// <c>Playhead</c>（带动 Slider 双向绑定 / TextBlock 等控件刷新一遍），随后 Avalonia
    /// 走它的 BringDescendantIntoView 流水线，会让 <c>RollScroll.Offset.X</c> 在我们这次
    /// 重新读取时短暂返回 0（实际真实偏移没有改变，<c>ScrollChanged</c> 也并未触发，是个
    /// 读取不一致问题），如果用这个 0 去判分支，就会误以为播放头在视野右侧好几屏外，
    /// 把视野挪去把播放头钉在最右边——也就是用户报告的 bug。所以把"点击发生时的真实视野
    /// 边界"在最早的入口先记下来，下面的判定才稳。
    /// </remarks>
    private void EnsurePlayheadVisible(double playheadSeconds, double offX, double viewW)
    {
        if (Vm is null) return;
        double pps = Math.Max(0.001, Roll.PixelsPerSecond);
        double x = playheadSeconds * pps;
        double newOffX = offX;
        if (x < offX)
            newOffX = Math.Max(0, x);
        else if (x > offX + viewW)
            newOffX = Math.Max(0, x - viewW);

        // 在 PianoRoll 的 PointerPressed 处理路径里，Avalonia 还会顺手把宿主 ScrollViewer
        // 的 Offset.X 重置（看起来是 BringDescendantIntoView 之类的连带效果），并且不会
        // 触发 ScrollChanged。这意味着即使我们计算出"无需滚动"，Offset.X 也已经被改到 0
        // 之类的值——视野直接被拽回曲谱开头。所以这里跟"当前真实 Offset"对比，差得多
        // 就显式写回 newOffX，把 Avalonia 的副作用反悔掉。
        double curOffX = RollScroll.Offset.X;
        if (Math.Abs(newOffX - curOffX) > 0.5)
            RollScroll.Offset = new Vector(newOffX, RollScroll.Offset.Y);
    }

    private void OnRollSeek(double seconds)
    {
        // 抓拍点击发生时的"真实视野起点 / 视野宽度"。
        // - viewW 直接读 RollScroll.Viewport.Width，目前观察是稳的；
        // - offX 不能直接读 RollScroll.Offset.X：在 PianoRoll PointerPressed → Vm.Seek 这条
        //   路径里，Avalonia 内部会顺手把 ScrollViewer 的 Offset.X 重置（看起来是
        //   BringDescendantIntoView 的连带效果），而且不会触发 ScrollChanged。直接读会拿到
        //   一个错乱的瞬态值（往往是 0），导致 EnsurePlayheadVisible 误以为播放头跑到右侧
        //   几屏外、把视野硬挪去对齐——也就是用户报告的 bug。
        //   这里改读 Vm.ViewportStart——它是 ScrollChanged → UpdateViewport 同步写进 VM 的
        //   "真实视野起点（秒）"，跟点击时刻 Avalonia 的瞬态状态无关；只有当它跟当前物理
        //   Offset 一致时才用 raw 值。
        double pps = Math.Max(0.001, Roll.PixelsPerSecond);
        double rawOffX = RollScroll.Offset.X;
        double vmOffX = Vm is null ? rawOffX : Vm.ViewportStart * pps;
        double offX = (Vm is null) ? rawOffX
                                   : (Math.Abs(rawOffX - vmOffX) > 1.0 ? vmOffX : rawOffX);
        double viewW = RollScroll.Viewport.Width;
        Vm?.Seek(seconds);
        EnsurePlayheadVisible(seconds, offX, viewW);
    }

    private void OnTimelineDragFinished(object? sender, PointerReleasedEventArgs e)
    {
        if (Vm is null) return;
        Vm.IsDraggingTimeline = false;
        double target = TimelineSlider.Value;
        double offX = RollScroll.Offset.X;
        double viewW = RollScroll.Viewport.Width;
        Vm.Seek(target);
        EnsurePlayheadVisible(target, offX, viewW);
    }

    // Ctrl + 滚轮：以鼠标位置为中心缩放钢琴卷帘
    private void OnRollWheel(object? sender, PointerWheelEventArgs e)
    {
        if (Vm is null) return;
        if ((e.KeyModifiers & KeyModifiers.Control) == 0) return;

        const double minPps = 20;
        const double maxPps = 600;

        double oldPps = Vm.PixelsPerSecond;
        double factor = e.Delta.Y > 0 ? 1.15 : 1.0 / 1.15;
        double newPps = Math.Clamp(oldPps * factor, minPps, maxPps);
        if (Math.Abs(newPps - oldPps) < 0.01)
        {
            e.Handled = true;
            return;
        }

        double mouseX = e.GetPosition(RollScroll).X;
        double ratio = newPps / oldPps;
        double oldOffsetX = RollScroll.Offset.X;
        double newOffsetX = Math.Max(0, (oldOffsetX + mouseX) * ratio - mouseX);

        // 关键：先改 pps，再立即强制布局，让 ScrollViewer 的 Extent 反映新宽度，
        // 然后在同一帧内把 Offset 设置好，这样不会先渲染一个错误位置的帧再回弹。
        Vm.PixelsPerSecond = newPps;
        Roll.InvalidateMeasure();
        Roll.UpdateLayout();
        RollScroll.UpdateLayout();

        double maxOff = Math.Max(0, RollScroll.Extent.Width - RollScroll.Viewport.Width);
        newOffsetX = Math.Clamp(newOffsetX, 0, maxOff);
        RollScroll.Offset = new Vector(newOffsetX, RollScroll.Offset.Y);

        e.Handled = true;
    }

    /// <summary>
    /// 拖拽悬停：只要拖入数据声明里包含文件就接受 Copy 效果。
    /// 真正的 .mid/.midi 后缀过滤放到 OnDrop 里做——某些平台在 DragOver 阶段
    /// IStorageItem.TryGetLocalPath 还拿不到本地路径，过早过滤会让用户一直看到禁止图标。
    /// </summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (HasAnyFile(e))
            e.DragEffects = DragDropEffects.Copy;
        else
            e.DragEffects = DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// 拖拽释放：取第一个 MIDI 文件的本地路径并调用 VM 的加载流程。
    /// 如果拖入的文件里一个 .mid/.midi 都没有，给出友好提示而不是静默失败。
    /// </summary>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        try
        {
            var path = TryGetFirstMidiPath(e);
            if (path == null)
            {
                if (Vm != null && HasAnyFile(e))
                    Vm.StatusText = "请拖入 .mid 或 .midi 文件";
                return;
            }
            if (Vm != null)
                await Vm.LoadMidiAsync(path);
        }
        catch (Exception ex)
        {
            if (Vm != null) Vm.StatusText = $"拖入文件失败: {ex.Message}";
        }
        finally
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// 拖入数据里是否至少包含一个文件条目（不校验后缀）。
    /// 用 try/catch 保护，避免某些平台 DragOver 阶段访问 GetFiles 抛异常。
    /// </summary>
    private static bool HasAnyFile(DragEventArgs e)
    {
        try
        {
            if (!e.Data.Contains(DataFormats.Files)) return false;
            var files = e.Data.GetFiles();
            if (files == null) return false;
            foreach (var _ in files) return true;
            return false;
        }
        catch
        {
            return e.Data.Contains(DataFormats.Files);
        }
    }

    /// <summary>
    /// 从拖拽事件里挑出第一个 .mid/.midi 文件的本地路径；找不到返回 null。
    /// </summary>
    private static string? TryGetFirstMidiPath(DragEventArgs e)
    {
        IEnumerable<Avalonia.Platform.Storage.IStorageItem>? files;
        try { files = e.Data.GetFiles(); }
        catch { return null; }
        if (files == null) return null;
        foreach (var item in files)
        {
            var p = item.TryGetLocalPath();
            if (string.IsNullOrEmpty(p)) continue;
            var ext = System.IO.Path.GetExtension(p);
            if (ext.Equals(".mid", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".midi", StringComparison.OrdinalIgnoreCase))
            {
                return p;
            }
        }
        return null;
    }

    private async void OnOpenClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var provider = StorageProvider;
            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 MIDI 文件",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("MIDI 文件")
                    {
                        Patterns = new[] { "*.mid", "*.midi" }
                    },
                    FilePickerFileTypes.All,
                }
            });
            if (files.Count == 0) return;
            var path = files[0].TryGetLocalPath();
            if (!string.IsNullOrEmpty(path) && Vm != null)
                await Vm.LoadMidiAsync(path);
        }
        catch (Exception ex)
        {
            if (Vm != null) Vm.StatusText = $"打开文件失败: {ex.Message}";
        }
    }

    private async void OnImportLetterScoreClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var dialog = new LetterScoreDialog();
        await dialog.ShowDialog(this);
        if (!string.IsNullOrWhiteSpace(dialog.ResultText))
        {
            Vm.LoadFromLetterScore(dialog.ResultText);
        }
    }

    private async void OnExportMidiClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || Vm.Tracks.Count == 0)
        {
            if (Vm != null) Vm.StatusText = "没有可导出的轨道";
            return;
        }

        try
        {
            var provider = StorageProvider;
            // 生成建议文件名：去掉原扩展名后加 .mid
            string suggested = Vm.FileName ?? "export";
            var dotIdx = suggested.LastIndexOf('.');
            if (dotIdx > 0) suggested = suggested[..dotIdx];

            var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出 MIDI 文件",
                DefaultExtension = "mid",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("MIDI 文件") { Patterns = new[] { "*.mid" } },
                },
                SuggestedFileName = suggested,
            });
            if (file == null) return;
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
                Vm.ExportMidi(path);
        }
        catch (Exception ex)
        {
            if (Vm != null) Vm.StatusText = $"导出失败: {ex.Message}";
        }
    }

    private void OnHotKey(object? sender, KeyEventArgs e)
    {
        if (Vm is null) return;
        switch (e.Key)
        {
            case Key.Space:
                Vm.TogglePlayPause();
                e.Handled = true;
                break;
            case Key.Home:
            {
                double offX = RollScroll.Offset.X;
                double viewW = RollScroll.Viewport.Width;
                Vm.Seek(0);
                EnsurePlayheadVisible(0, offX, viewW);
                e.Handled = true;
                break;
            }
        }
    }

    // ========== 多轨交互 ==========

    /// <summary>
    /// 点击轨道头：只切换到该轨道，不移动视野。
    /// </summary>
    private void OnTrackHeadPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is null) return;
        if (sender is Control c && c.Tag is MidiTrack track)
        {
            Vm.SelectedTrack = track;
            e.Handled = true;
        }
    }

    /// <summary>
    /// 轨道缩略图上的点击（视野框外）：同时切换轨道并把视野滚到此处。
    /// </summary>
    private void OnStripSeekAndActivate(MidiTrack track, double newStartSec)
    {
        if (Vm is null) return;
        Vm.SelectedTrack = track;
        ScrollRollTo(newStartSec);
    }

    /// <summary>
    /// 拖拽视野框：只移动视野，不改变当前轨道（拖拽只在当前轨道可以发起）。
    /// </summary>
    private void OnStripViewportDragTo(double newStartSec)
    {
        ScrollRollTo(newStartSec);
    }

    /// <summary>
    /// 速度轨上完成 BPM 编辑：把 (marker, newBpm) 透传给 ViewModel；
    /// 真正的 TempoManager.SetBpm + 重算 Note 秒时间在 VM 里完成。
    /// </summary>
    private void OnTempoBpmChangeRequested(Models.TempoMarker marker, double newBpm)
    {
        Vm?.RequestBpmChange(marker, newBpm);
    }

    private void ScrollRollTo(double newStartSec)
    {
        if (Vm is null) return;
        double pps = Math.Max(0.001, Roll.PixelsPerSecond);
        double maxOff = Math.Max(0, RollScroll.Extent.Width - RollScroll.Viewport.Width);
        double x = Math.Clamp(newStartSec * pps, 0, maxOff);
        RollScroll.Offset = new Vector(x, RollScroll.Offset.Y);
    }
}
