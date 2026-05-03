using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using GenshinLyrePlayer.Models;

namespace GenshinLyrePlayer.Controls;

/// <summary>
/// 单条轨道的缩略图。横向压缩整首曲子的全部时长，纵向压缩音高范围；
/// 在当前选中轨道上会绘制一个“视野框”，表示钢琴卷帘对应的可见区域。
///
/// 交互：
/// - 在视野框内按下：进入拖拽模式，框会跟着指针左右移动；
/// - 在视野框外按下 / 在其它轨道上点击：发出 <see cref="SeekAndActivate"/>，
///   将该轨道设为当前轨道并把视野中心移到点击处；
/// - 纯点击轨道头（由外部处理）只切换当前轨道，不动视野。
/// </summary>
public sealed class TrackStrip : Control
{
    // ===== 依赖属性 =====

    public static readonly StyledProperty<MidiTrack?> TrackProperty =
        AvaloniaProperty.Register<TrackStrip, MidiTrack?>(nameof(Track));

    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<TrackStrip, double>(nameof(Duration));

    public static readonly StyledProperty<double> PlayheadProperty =
        AvaloniaProperty.Register<TrackStrip, double>(nameof(Playhead));

    public static readonly StyledProperty<double> ViewportStartProperty =
        AvaloniaProperty.Register<TrackStrip, double>(nameof(ViewportStart));

    public static readonly StyledProperty<double> ViewportEndProperty =
        AvaloniaProperty.Register<TrackStrip, double>(nameof(ViewportEnd));

    public static readonly StyledProperty<MidiTrack?> SelectedTrackProperty =
        AvaloniaProperty.Register<TrackStrip, MidiTrack?>(nameof(SelectedTrack));

    public MidiTrack? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public double Duration { get => GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double Playhead { get => GetValue(PlayheadProperty); set => SetValue(PlayheadProperty, value); }
    public double ViewportStart { get => GetValue(ViewportStartProperty); set => SetValue(ViewportStartProperty, value); }
    public double ViewportEnd { get => GetValue(ViewportEndProperty); set => SetValue(ViewportEndProperty, value); }
    public MidiTrack? SelectedTrack { get => GetValue(SelectedTrackProperty); set => SetValue(SelectedTrackProperty, value); }

    /// <summary>点击非视野框区域：请求将此轨道设为当前，并把视野滚到此时间（秒，视野起点）。</summary>
    public event Action<MidiTrack, double>? SeekAndActivate;

    /// <summary>正在拖拽当前轨道的视野框：参数是新的视野起点（秒）。</summary>
    public event Action<double>? ViewportDragTo;

    // ===== 外观常量 =====

    private const int MinPitch = 36;  // C2
    private const int MaxPitch = 96;  // C7

    private static readonly IBrush ActiveBg = new SolidColorBrush(Color.FromRgb(32, 36, 46));
    private static readonly IBrush InactiveBg = new SolidColorBrush(Color.FromRgb(22, 24, 30));
    private static readonly IPen BorderPen = new Pen(new SolidColorBrush(Color.FromRgb(42, 45, 54)), 1);
    private static readonly IPen PlayheadPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 210, 80)), 1);
    private static readonly IPen ViewportPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 210, 80)), 1.5);
    private static readonly IBrush ViewportFill = new SolidColorBrush(Color.FromArgb(38, 255, 210, 80));
    private static readonly IBrush UnsupportedBrush = new SolidColorBrush(Color.FromRgb(220, 90, 90));

    static TrackStrip()
    {
        AffectsRender<TrackStrip>(
            TrackProperty, DurationProperty, PlayheadProperty,
            ViewportStartProperty, ViewportEndProperty, SelectedTrackProperty);
    }

    public TrackStrip()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TrackProperty)
            OnTrackChanged(change.GetNewValue<MidiTrack?>());
    }

    private System.ComponentModel.PropertyChangedEventHandler? _trackHandler;
    private MidiTrack? _hookedTrack;

    private void OnTrackChanged(MidiTrack? tr)
    {
        if (_hookedTrack != null && _trackHandler != null)
            _hookedTrack.PropertyChanged -= _trackHandler;

        _hookedTrack = tr;
        _trackHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(MidiTrack.Notes) ||
                e.PropertyName == nameof(MidiTrack.Muted) ||
                e.PropertyName == nameof(MidiTrack.Name))
            {
                InvalidateVisual();
            }
        };
        if (tr != null)
            tr.PropertyChanged += _trackHandler;
    }

    private bool IsActive => Track != null && ReferenceEquals(Track, SelectedTrack);

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        double w = bounds.Width;
        double h = bounds.Height;
        if (w <= 0 || h <= 0) return;

        var bg = IsActive ? ActiveBg : InactiveBg;
        context.FillRectangle(bg, new Rect(0, 0, w, h));
        context.DrawLine(BorderPen, new Point(0, h - 0.5), new Point(w, h - 0.5));

        var tr = Track;
        if (tr == null) return;

        double dur = Math.Max(0.001, Duration);
        double xScale = w / dur;
        int pitchRange = MaxPitch - MinPitch;
        double yScale = (h - 2) / pitchRange;

        // 计算颜色：不活动 / 静音时做降亮处理。
        var accent = ArgbToBrush(tr.ColorArgb, IsActive ? (tr.Muted ? 120u : 255u) : (tr.Muted ? 70u : 160u));
        var accentUnsupported = ArgbToBrush(0xE65050, IsActive ? (tr.Muted ? 120u : 255u) : (tr.Muted ? 70u : 160u));

        // 绘制音符：极小的矩形/线段
        double noteH = Math.Max(1.2, Math.Min(3, yScale));
        foreach (var n in tr.Notes)
        {
            int p = Math.Clamp(n.EffectivePitch == 0 ? n.OriginalPitch : n.EffectivePitch, MinPitch, MaxPitch);
            double x = n.Start * xScale;
            double nw = Math.Max(1.0, n.Duration * xScale);
            double y = 1 + (MaxPitch - p) * yScale;
            var brush = n.Supported ? accent : accentUnsupported;
            context.FillRectangle(brush, new Rect(x, y, nw, noteH));
        }

        // 播放头
        if (Playhead > 0 && Playhead <= dur)
        {
            double px = Playhead * xScale;
            context.DrawLine(PlayheadPen, new Point(px, 0), new Point(px, h));
        }

        // 视野框（仅当前轨道绘制）
        if (IsActive)
        {
            double vpS = Math.Clamp(ViewportStart, 0, dur);
            double vpE = Math.Clamp(ViewportEnd, vpS, dur);
            if (vpE > vpS)
            {
                double x1 = vpS * xScale;
                double x2 = vpE * xScale;
                // 宽度至少 2px，否则看不见
                if (x2 - x1 < 2) x2 = x1 + 2;
                var rect = new Rect(x1, 0.5, x2 - x1, h - 1);
                context.FillRectangle(ViewportFill, rect);
                context.DrawRectangle(null, ViewportPen, rect);
            }
        }
    }

    // ===== 指针交互 =====

    private bool _dragging;
    private double _dragOffsetSec; // 指针时间 - ViewportStart

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Track == null || Duration <= 0) return;

        double w = Bounds.Width;
        if (w <= 0) return;

        double clickTime = e.GetPosition(this).X / w * Duration;

        // 是否在当前视野框内按下？仅当前轨道才支持拖拽。
        bool insideViewport =
            IsActive &&
            clickTime >= ViewportStart &&
            clickTime <= ViewportEnd;

        if (insideViewport)
        {
            _dragging = true;
            _dragOffsetSec = clickTime - ViewportStart;
            e.Pointer.Capture(this);
        }
        else
        {
            // 其它情况：跳到此处 + 激活当前轨道
            double vpLen = Math.Max(0.001, ViewportEnd - ViewportStart);
            double newStart = Math.Max(0, clickTime - vpLen / 2);
            SeekAndActivate?.Invoke(Track, newStart);

            // 按住继续拖拽
            _dragging = true;
            _dragOffsetSec = vpLen / 2;
            e.Pointer.Capture(this);
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging || !Equals(e.Pointer.Captured, this)) return;
        if (Duration <= 0) return;

        double w = Bounds.Width;
        if (w <= 0) return;

        double curTime = e.GetPosition(this).X / w * Duration;
        double newStart = curTime - _dragOffsetSec;
        double vpLen = Math.Max(0.001, ViewportEnd - ViewportStart);
        newStart = Math.Clamp(newStart, 0, Math.Max(0, Duration - vpLen));
        ViewportDragTo?.Invoke(newStart);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Equals(e.Pointer.Captured, this)) e.Pointer.Capture(null);
        _dragging = false;
    }

    private static IBrush ArgbToBrush(uint argb, uint forcedAlpha)
    {
        byte a = (byte)forcedAlpha;
        byte r = (byte)((argb >> 16) & 0xFF);
        byte g = (byte)((argb >> 8) & 0xFF);
        byte b = (byte)(argb & 0xFF);
        return new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }
}
