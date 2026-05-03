using System;
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

        // 安装全局键盘钩子：即使焦点不在本窗口（例如在原神里）也能响应 F8/F9
        Opened += (_, _) =>
        {
            GlobalKeyHook.KeyDown += OnGlobalKey;
            GlobalKeyHook.Install();
            UpdateViewport();
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

    private void AutoScrollToPlayhead()
    {
        if (Vm is null) return;
        double x = Vm.Playhead * Roll.PixelsPerSecond;
        double viewW = RollScroll.Viewport.Width;
        double offX = RollScroll.Offset.X;
        if (x > offX + viewW * 0.75)
            RollScroll.Offset = new Vector(Math.Max(0, x - viewW * 0.25), RollScroll.Offset.Y);
        else if (x < offX)
            RollScroll.Offset = new Vector(Math.Max(0, x - 20), RollScroll.Offset.Y);
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

        Vm.PixelsPerSecond = newPps;

        Dispatcher.UIThread.Post(() =>
        {
            RollScroll.Offset = new Vector(newOffsetX, RollScroll.Offset.Y);
        }, DispatcherPriority.Background);

        e.Handled = true;
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
