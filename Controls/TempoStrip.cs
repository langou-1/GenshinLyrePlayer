using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GenshinLyrePlayer.Models;

namespace GenshinLyrePlayer.Controls;

/// <summary>
/// 速度轨：横向跨满整首曲子，仅以"曲速标签 + 竖线"的形式显示
/// 每一个 SetTempo 事件的 BPM。播放头同步显示。
/// </summary>
public sealed class TempoStrip : Control
{
    public static readonly StyledProperty<IReadOnlyList<TempoMarker>?> TemposProperty =
        AvaloniaProperty.Register<TempoStrip, IReadOnlyList<TempoMarker>?>(nameof(Tempos));

    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<TempoStrip, double>(nameof(Duration));

    public static readonly StyledProperty<double> PlayheadProperty =
        AvaloniaProperty.Register<TempoStrip, double>(nameof(Playhead));

    public IReadOnlyList<TempoMarker>? Tempos
    {
        get => GetValue(TemposProperty);
        set => SetValue(TemposProperty, value);
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

    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(20, 22, 28));
    private static readonly IPen BorderPen = new Pen(new SolidColorBrush(Color.FromRgb(42, 45, 54)), 1);
    private static readonly IBrush MarkerBrush = new SolidColorBrush(Color.FromRgb(255, 210, 80));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromRgb(255, 230, 150));
    private static readonly IBrush LabelBackground = new SolidColorBrush(Color.FromArgb(200, 35, 38, 46));
    private static readonly IPen PlayheadPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 210, 80)), 1);
    private static readonly Typeface LabelTypeface = new("Inter");

    static TempoStrip()
    {
        AffectsRender<TempoStrip>(TemposProperty, DurationProperty, PlayheadProperty);
    }

    public TempoStrip()
    {
        ClipToBounds = true;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        double w = bounds.Width;
        double h = bounds.Height;
        if (w <= 0 || h <= 0) return;

        context.FillRectangle(BackgroundBrush, new Rect(0, 0, w, h));
        context.DrawLine(BorderPen, new Point(0, h - 0.5), new Point(w, h - 0.5));

        var tempos = Tempos;
        if (tempos == null || tempos.Count == 0) return;

        double dur = Math.Max(0.001, Duration);
        double xScale = w / dur;

        // 先准备所有标签的几何信息，再按从左到右的顺序绘制：
        //   - 短竖线总会画（让用户看到所有曲速点位置）；
        //   - 文本会做"互不重叠"的取舍，靠右的标签如果撞到上一个就跳过，避免一坨字糊在一起。
        double lastTextRight = double.NegativeInfinity;
        const double labelGap = 4;
        double midY = h * 0.5;

        for (int i = 0; i < tempos.Count; i++)
        {
            var m = tempos[i];
            double x = m.Time * xScale;
            if (x < -1 || x > w + 1) continue;

            // 标签竖线
            context.FillRectangle(MarkerBrush, new Rect(x, 0, 1, h));

            // 文本：BPM 保留 1 位小数（整数 BPM 显示成 120.0 也能接受，简单清晰）
            string txt = m.Bpm.ToString("F1", CultureInfo.InvariantCulture);
            var ft = new FormattedText(
                txt,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                10,
                LabelBrush);

            double tw = ft.Width + 8;
            double tx = x + 2;
            double ty = midY - ft.Height / 2;

            // 撞车就丢弃；否则画一个半透明底以提高在音轨色块上的可读性
            if (tx < lastTextRight + labelGap) continue;
            if (tx + tw > w) continue;

            context.FillRectangle(LabelBackground, new Rect(tx, ty - 1, tw, ft.Height + 2));
            context.DrawText(ft, new Point(tx + 4, ty));
            lastTextRight = tx + tw;
        }

        // 播放头
        if (Playhead > 0 && Playhead <= dur)
        {
            double px = Playhead * xScale;
            context.DrawLine(PlayheadPen, new Point(px, 0), new Point(px, h));
        }
    }
}
