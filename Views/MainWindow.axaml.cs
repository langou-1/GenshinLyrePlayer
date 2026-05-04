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
            if (e.PropertyName == nameof(MainWindowViewModel.Playhead))
                Dispatcher.UIThread.Post(AutoScrollToPlayhead);
            else if (e.PropertyName == nameof(MainWindowViewModel.Duration)
                  || e.PropertyName == nameof(MainWindowViewModel.PixelsPerSecond))
                Dispatcher.UIThread.Post(UpdateViewport);
        };
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
    /// 跟随 Playhead 自动滚动视野。参考 TuneLab 的策略：
    /// <list type="bullet">
    /// <item>播放中：Playhead 靠近视野右侧（>75%）时翻一页，让接下来要演奏的内容露出来。</item>
    /// <item>未播放：仅在 Playhead 跑到可视范围之外时，把视野挪到刚好能看见 Playhead 的位置；
    ///       用户 Seek 到当前视野内的位置时视野完全不动。</item>
    /// </list>
    /// </summary>
    private void AutoScrollToPlayhead()
    {
        if (Vm is null) return;
        double x = Vm.Playhead * Roll.PixelsPerSecond;
        double viewW = RollScroll.Viewport.Width;
        double offX = RollScroll.Offset.X;

        if (Vm.IsPlaying)
        {
            // 播放：靠近右侧 25% 范围内就翻一页；不再处理"playhead 跳到左边"的情况，
            // 因为播放过程中 Playhead 只会向前推进，向后跳一定来自用户 Seek，
            // 那种情况由下面"未播放"分支或者播放中 Seek 的语义共同决定——
            // 用户在播放中 Seek 后 Player 会重新启动并继续向前推进，这里依然按右侧阈值翻页即可。
            if (x > offX + viewW * 0.75)
                RollScroll.Offset = new Vector(Math.Max(0, x - viewW * 0.25), RollScroll.Offset.Y);
        }
        else
        {
            // 未播放：用户 Seek 到视野内则保持不动；只有跑出视野才把视野挪到能看见 Playhead 的位置。
            if (x < offX)
                RollScroll.Offset = new Vector(Math.Max(0, x), RollScroll.Offset.Y);
            else if (x > offX + viewW)
                RollScroll.Offset = new Vector(Math.Max(0, x - viewW), RollScroll.Offset.Y);
        }
    }

    private void OnRollSeek(double seconds) => Vm?.Seek(seconds);

    private void OnTimelineReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (Vm is null) return;
        Vm.Seek(TimelineSlider.Value);
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
                Vm.Seek(0);
                e.Handled = true;
                break;
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
