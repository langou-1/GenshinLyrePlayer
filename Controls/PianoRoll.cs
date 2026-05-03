using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using GenshinLyrePlayer.Models;
using GenshinLyrePlayer.Services;

namespace GenshinLyrePlayer.Controls;

/// <summary>
/// 自绘钢琴卷帘：横轴为时间，纵轴为音高。支持点击跳转、播放头显示。
/// </summary>
public sealed class PianoRoll : Control
{
    // ======= 可绑定属性 =======

    public static readonly StyledProperty<IReadOnlyList<Note>?> NotesProperty =
        AvaloniaProperty.Register<PianoRoll, IReadOnlyList<Note>?>(nameof(Notes));

    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<PianoRoll, double>(nameof(Duration), 0);

    public static readonly StyledProperty<double> PlayheadProperty =
        AvaloniaProperty.Register<PianoRoll, double>(nameof(Playhead), 0);

    public static readonly StyledProperty<double> PixelsPerSecondProperty =
        AvaloniaProperty.Register<PianoRoll, double>(nameof(PixelsPerSecond), 120);

    public static readonly StyledProperty<InstrumentGroup?> InstrumentGroupProperty =
        AvaloniaProperty.Register<PianoRoll, InstrumentGroup?>(nameof(InstrumentGroup));

    public IReadOnlyList<Note>? Notes
    {
        get => GetValue(NotesProperty);
        set => SetValue(NotesProperty, value);
    }

    public double Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public double Playhead
    {
        get => GetValue(PlayheadProperty);
        set => SetValue(PlayheadProperty, value);
    }

    public double PixelsPerSecond
    {
        get => GetValue(PixelsPerSecondProperty);
        set => SetValue(PixelsPerSecondProperty, value);
    }

    /// <summary>当前乐器组。用于确定“可演奏音域”高亮区域。为空时回退到 <see cref="Instruments.Default"/>。</summary>
    public InstrumentGroup? InstrumentGroup
    {
        get => GetValue(InstrumentGroupProperty);
        set => SetValue(InstrumentGroupProperty, value);
    }

    /// <summary>用户点击/拖动导致的跳转请求。参数：秒。</summary>
    public event Action<double>? SeekRequested;

    // ======= 外观常量 =======

    private const int MinPitchShown = 36;   // C2
    private const int MaxPitchShown = 96;   // C7
    private const double RowHeight = 8;

    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(28, 30, 36));
    private static readonly IBrush RowBrushA = new SolidColorBrush(Color.FromRgb(34, 37, 44));
    private static readonly IBrush RowBrushB = new SolidColorBrush(Color.FromRgb(30, 33, 40));
    private static readonly IBrush PlayableBandBrush = new SolidColorBrush(Color.FromArgb(40, 120, 200, 255));
    private static readonly IBrush OctaveLineBrush = new SolidColorBrush(Color.FromArgb(80, 200, 200, 220));
    private static readonly IBrush BarLineBrush = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255));
    private static readonly IBrush SupportedBrush = new SolidColorBrush(Color.FromRgb(90, 200, 140));
    private static readonly IBrush SupportedBorder = new SolidColorBrush(Color.FromRgb(30, 120, 80));
    private static readonly IBrush UnsupportedBrush = new SolidColorBrush(Color.FromRgb(230, 85, 85));
    private static readonly IBrush UnsupportedBorder = new SolidColorBrush(Color.FromRgb(140, 30, 30));
    private static readonly IPen PlayheadPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 210, 80)), 2);
    private static readonly IPen OctavePen = new Pen(OctaveLineBrush, 1);
    private static readonly IPen BarPen = new Pen(BarLineBrush, 1);
    private static readonly Typeface LabelTypeface = new("Inter");
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromArgb(180, 220, 220, 220));

    static PianoRoll()
    {
        AffectsRender<PianoRoll>(NotesProperty, DurationProperty, PlayheadProperty, PixelsPerSecondProperty, InstrumentGroupProperty);
        AffectsMeasure<PianoRoll>(DurationProperty, PixelsPerSecondProperty);
    }

    public PianoRoll()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = Math.Max(200, Duration * PixelsPerSecond + 40);
        double height = (MaxPitchShown - MinPitchShown + 1) * RowHeight;
        return new Size(width, height);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(BackgroundBrush, bounds);

        int rows = MaxPitchShown - MinPitchShown + 1;
        double width = bounds.Width;
        double height = rows * RowHeight;

        // 行背景（黑白键交替）
        for (int i = 0; i < rows; i++)
        {
            int pitch = MaxPitchShown - i;
            var brush = IsBlackKey(pitch) ? RowBrushB : RowBrushA;
            context.FillRectangle(brush, new Rect(0, i * RowHeight, width, RowHeight));
        }

        // 可演奏音域高亮（依赖当前乐器组）
        var group = InstrumentGroup ?? Instruments.Default;
        double playableTopY = PitchToY(group.MaxPitch);
        double playableBotY = PitchToY(group.MinPitch) + RowHeight;
        context.FillRectangle(PlayableBandBrush, new Rect(0, playableTopY, width, playableBotY - playableTopY));

        // 八度分隔线（每个 C 音）
        for (int p = MinPitchShown; p <= MaxPitchShown; p++)
        {
            if (p % 12 == 0)
            {
                double y = PitchToY(p) + RowHeight;
                context.DrawLine(OctavePen, new Point(0, y), new Point(width, y));
                // 左侧标签
                var txt = new FormattedText($"C{p / 12 - 1}",
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, LabelTypeface, 10, LabelBrush);
                context.DrawText(txt, new Point(4, y - RowHeight - 1));
            }
        }

        // 每秒一条节拍线
        if (Duration > 0)
        {
            for (double t = 1; t <= Duration; t += 1)
            {
                double x = t * PixelsPerSecond;
                context.DrawLine(BarPen, new Point(x, 0), new Point(x, height));
            }
        }

        // 音符
        var notes = Notes;
        if (notes != null)
        {
            foreach (var n in notes)
            {
                int pitch = n.EffectivePitch;
                if (pitch < MinPitchShown || pitch > MaxPitchShown)
                {
                    // 越界的音夹到边缘再标红
                    pitch = Math.Clamp(pitch, MinPitchShown, MaxPitchShown);
                }

                double x = n.Start * PixelsPerSecond;
                double w = Math.Max(3, n.Duration * PixelsPerSecond);
                double y = PitchToY(pitch);
                var rect = new Rect(x, y + 1, w, RowHeight - 2);

                if (n.Supported)
                {
                    context.FillRectangle(SupportedBrush, rect, 2);
                    context.DrawRectangle(null, new Pen(SupportedBorder, 1), rect, 2, 2);
                }
                else
                {
                    context.FillRectangle(UnsupportedBrush, rect, 2);
                    context.DrawRectangle(null, new Pen(UnsupportedBorder, 1), rect, 2, 2);
                }
            }
        }

        // 播放头
        double phX = Playhead * PixelsPerSecond;
        context.DrawLine(PlayheadPen, new Point(phX, 0), new Point(phX, height));
    }

    private double PitchToY(int pitch) => (MaxPitchShown - pitch) * RowHeight;

    private static bool IsBlackKey(int pitch)
    {
        int pc = ((pitch % 12) + 12) % 12;
        return pc == 1 || pc == 3 || pc == 6 || pc == 8 || pc == 10;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        RaiseSeek(e);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Equals(e.Pointer.Captured, this)) RaiseSeek(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Equals(e.Pointer.Captured, this)) e.Pointer.Capture(null);
    }

    private void RaiseSeek(PointerEventArgs e)
    {
        double x = e.GetPosition(this).X;
        if (PixelsPerSecond <= 0) return;
        double t = Math.Max(0, x / PixelsPerSecond);
        if (Duration > 0) t = Math.Min(t, Duration);
        SeekRequested?.Invoke(t);
    }
}
