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
        DataContextChanged += (_, _) => HookVm();
        KeyDown += OnHotKey;

        // 安装全局键盘钩子：即使焦点不在本窗口（例如在原神里）也能响应 F8/F9
        Opened += (_, _) =>
        {
            GlobalKeyHook.KeyDown += OnGlobalKey;
            GlobalKeyHook.Install();
        };
        Closed += (_, _) =>
        {
            GlobalKeyHook.KeyDown -= OnGlobalKey;
            GlobalKeyHook.Uninstall();
        };
    }

    private void OnGlobalKey(uint vk)
    {
        // 钩子回调可能在非 UI 线程，切回 UI 线程处理
        Dispatcher.UIThread.Post(() =>
        {
            if (Vm is null) return;
            switch (vk)
            {
                case VK_F8:  // 播放 / 暂停 切换
                    Vm.TogglePlayPause();
                    break;
                case VK_F9:  // 停止
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
        };
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

    private void OnRollSeek(double seconds)
    {
        Vm?.Seek(seconds);
    }

    private void OnTimelineReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (Vm is null) return;
        Vm.Seek(TimelineSlider.Value);
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
}
