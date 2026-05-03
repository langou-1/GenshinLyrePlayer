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
///
/// 格线绘制策略（参考 TuneLab 的钢琴窗）：
/// <list type="bullet">
///   <item>不再使用"每秒一条"的非音乐格线；</item>
///   <item>由 <see cref="TimeSignatureManager"/> 给出 bar/beat 的 tick 边界；</item>
///   <item>由 <see cref="TempoManager"/> 把 tick 折算到秒；</item>
///   <item>本类把秒乘 <see cref="PixelsPerSecond"/> 得到屏幕 X，绘制小节线 / 拍线 /（按缩放级别淡入的）8 分线。</item>
/// </list>
/// 在曲速变化的段落里，每个 bar 的视觉宽度会自动随 BPM 改变，但格线始终对齐音乐节拍。
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

    public static readonly StyledProperty<TempoManager?> TempoManagerProperty =
        AvaloniaProperty.Register<PianoRoll, TempoManager?>(nameof(TempoManager));

    public static readonly StyledProperty<TimeSignatureManager?> TimeSignatureManagerProperty =
        AvaloniaProperty.Register<PianoRoll, TimeSignatureManager?>(nameof(TimeSignatureManager));

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

    /// <summary>当前乐器组。用于确定"可演奏音域"高亮区域。为空时回退到 <see cref="Instruments.Default"/>。</summary>
    public InstrumentGroup? InstrumentGroup
    {
        get => GetValue(InstrumentGroupProperty);
        set => SetValue(InstrumentGroupProperty, value);
    }

    /// <summary>曲速管理器：用于把 bar/beat 的 tick 边界折算到秒，再映射到 X。</summary>
    public TempoManager? TempoManager
    {
        get => GetValue(TempoManagerProperty);
        set => SetValue(TempoManagerProperty, value);
    }

    /// <summary>拍号管理器：决定每小节多少拍、每拍多少 tick。</summary>
    public TimeSignatureManager? TimeSignatureManager
    {
        get => GetValue(TimeSignatureManagerProperty);
        set => SetValue(TimeSignatureManagerProperty, value);
    }

    /// <summary>用户点击/拖动导致的跳转请求。参数：秒。</summary>
    public event Action<double>? SeekRequested;

    // ======= 外观常量 =======

    private const int MinPitchShown = 36;   // C2
    private const int MaxPitchShown = 96;   // C7
    private const double RowHeight = 8;

    /// <summary>拍线/分隔线开始淡入和完全显示的像素阈值（参考 TuneLab 的 MIN_GRID_GAP）。</summary>
    private const double MinGridGap = 6;
    private const double FullGridGap = 14;

    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(28, 30, 36));
    private static readonly IBrush RowBrushA = new SolidColorBrush(Color.FromRgb(34, 37, 44));
    private static readonly IBrush RowBrushB = new SolidColorBrush(Color.FromRgb(30, 33, 40));
    private static readonly IBrush PlayableBandBrush = new SolidColorBrush(Color.FromArgb(40, 120, 200, 255));
    private static readonly IBrush OctaveLineBrush = new SolidColorBrush(Color.FromArgb(80, 200, 200, 220));
    private static readonly IBrush SupportedBrush = new SolidColorBrush(Color.FromRgb(90, 200, 140));
    private static readonly IBrush SupportedBorder = new SolidColorBrush(Color.FromRgb(30, 120, 80));
    private static readonly IBrush UnsupportedBrush = new SolidColorBrush(Color.FromRgb(230, 85, 85));
    private static readonly IBrush UnsupportedBorder = new SolidColorBrush(Color.FromRgb(140, 30, 30));
    private static readonly IPen PlayheadPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 210, 80)), 2);
    private static readonly IPen OctavePen = new Pen(OctaveLineBrush, 1);
    private static readonly IPen BarLinePen = new Pen(new SolidColorBrush(Color.FromArgb(140, 220, 220, 230)), 1);
    private static readonly IPen FallbackSecondPen = new Pen(new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)), 1);
    private static readonly Typeface LabelTypeface = new("Inter");
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromArgb(180, 220, 220, 220));
    private static readonly IBrush BarLabelBrush = new SolidColorBrush(Color.FromArgb(160, 220, 220, 230));

    static PianoRoll()
    {
        AffectsRender<PianoRoll>(
            NotesProperty,
            DurationProperty,
            PlayheadProperty,
            PixelsPerSecondProperty,
            InstrumentGroupProperty,
            TempoManagerProperty,
            TimeSignatureManagerProperty);
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

        // —— 时间格线 ——
        // 优先按 tick（音乐意义）绘制；缺少 Tempo / TimeSignature 信息时回退到旧的"每秒一条"。
        if (Duration > 0)
        {
            var tempoMgr = TempoManager;
            var tsMgr = TimeSignatureManager;
            if (tempoMgr != null && tsMgr != null)
                DrawTickGrid(context, tempoMgr, tsMgr, width, height);
            else
                DrawSecondGrid(context, width, height);
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

    /// <summary>
    /// 旧的回退方案：每秒一条节拍线。仅在尚未拿到 TempoManager / TimeSignatureManager 时使用。
    /// </summary>
    private void DrawSecondGrid(DrawingContext context, double width, double height)
    {
        for (double t = 1; t <= Duration; t += 1)
        {
            double x = t * PixelsPerSecond;
            context.DrawLine(FallbackSecondPen, new Point(x, 0), new Point(x, height));
        }
    }

    /// <summary>
    /// 按 tick 绘制小节 / 拍 / 半拍格线，参考 TuneLab 的 PianoScrollView 分级淡入策略。
    /// </summary>
    private void DrawTickGrid(DrawingContext context, TempoManager tempoMgr, TimeSignatureManager tsMgr, double width, double height)
    {
        long endTick = Math.Max(0, tempoMgr.SecondsToTick(Duration));
        if (endTick <= 0) return;

        var markers = tsMgr.Markers;

        for (int i = 0; i < markers.Count; i++)
        {
            var ts = markers[i];
            long sectionEnd = i + 1 < markers.Count ? markers[i + 1].Tick : endTick;
            if (ts.Tick >= sectionEnd) continue;

            long ticksPerBar = tsMgr.TicksPerBar(ts);
            long ticksPerBeat = tsMgr.TicksPerBeat(ts);
            if (ticksPerBar <= 0 || ticksPerBeat <= 0) continue;

            // 把本段切成一个个 bar；最后一个 bar 可能只画一半（曲尾）。
            int barCount = (int)((sectionEnd - ts.Tick + ticksPerBar - 1) / ticksPerBar);

            // 1) 小节线 + 序号标签
            for (int b = 0; b <= barCount; b++)
            {
                long barTick = ts.Tick + (long)b * ticksPerBar;
                if (barTick > endTick) break;
                double xBar = tempoMgr.TickToSeconds(barTick) * PixelsPerSecond;
                context.DrawLine(BarLinePen, new Point(xBar, 0), new Point(xBar, height));

                // 在每条小节线左侧标"x"小节号（仅当本 bar 在本段内）
                if (b < barCount)
                {
                    int barNumber = ts.BarIndex + b + 1; // 1 起算更自然
                    var label = new FormattedText($"{barNumber}",
                        System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, LabelTypeface, 10, BarLabelBrush);
                    context.DrawText(label, new Point(xBar + 2, 1));
                }
            }

            // 2) 拍线 / 半拍线（按本 bar 的视觉宽度淡入；曲速段内宽度固定，跨段才会变）
            for (int b = 0; b < barCount; b++)
            {
                long barStartTick = ts.Tick + (long)b * ticksPerBar;
                if (barStartTick >= endTick) break;
                long barEndTick = Math.Min(barStartTick + ticksPerBar, endTick);

                double barStartSec = tempoMgr.TickToSeconds(barStartTick);
                double barEndSec = tempoMgr.TickToSeconds(barEndTick);
                double barPx = (barEndSec - barStartSec) * PixelsPerSecond;
                if (barPx <= 0) continue;

                // 拍线
                double beatPx = barPx / ts.Numerator;
                double beatOpacity = SmoothStep(MinGridGap, FullGridGap, beatPx);
                if (beatOpacity > 0)
                {
                    var beatPen = new Pen(
                        new SolidColorBrush(Color.FromArgb((byte)(80 * beatOpacity), 200, 200, 220)),
                        1);
                    for (int beat = 1; beat < ts.Numerator; beat++)
                    {
                        long beatTick = barStartTick + (long)beat * ticksPerBeat;
                        if (beatTick >= endTick) break;
                        double xBeat = tempoMgr.TickToSeconds(beatTick) * PixelsPerSecond;
                        context.DrawLine(beatPen, new Point(xBeat, 0), new Point(xBeat, height));
                    }
                }

                // 半拍线（每拍中点）：缩放更近时才出现
                double halfPx = beatPx / 2;
                double halfOpacity = SmoothStep(MinGridGap, FullGridGap, halfPx);
                if (halfOpacity > 0)
                {
                    var halfPen = new Pen(
                        new SolidColorBrush(Color.FromArgb((byte)(50 * halfOpacity), 200, 200, 220)),
                        1);
                    for (int beat = 0; beat < ts.Numerator; beat++)
                    {
                        long halfTick = barStartTick + (long)beat * ticksPerBeat + ticksPerBeat / 2;
                        if (halfTick >= endTick) break;
                        double xH = tempoMgr.TickToSeconds(halfTick) * PixelsPerSecond;
                        context.DrawLine(halfPen, new Point(xH, 0), new Point(xH, height));
                    }
                }
            }
        }
    }

    /// <summary>把 [a, b] 范围的输入做平滑过渡：≤a 输出 0，≥b 输出 1，区间内线性插值。</summary>
    private static double SmoothStep(double a, double b, double x)
    {
        if (x <= a) return 0;
        if (x >= b) return 1;
        return (x - a) / (b - a);
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
