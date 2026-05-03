using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using GenshinLyrePlayer.Models;

namespace GenshinLyrePlayer.Controls;

/// <summary>
/// 速度轨：横向跨满整首曲子，按时间比例画每一个 SetTempo 标签。
/// 单击某个标签会弹出一个内嵌 TextBox 让用户改写 BPM；提交后通过
/// <see cref="BpmChangeRequested"/> 事件把 (marker, newBpm) 抛给 ViewModel。
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

    /// <summary>用户在某个曲速标签上完成编辑。参数：被编辑的 marker + 新 BPM。</summary>
    public event Action<TempoMarker, double>? BpmChangeRequested;

    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(20, 22, 28));
    private static readonly IPen BorderPen = new Pen(new SolidColorBrush(Color.FromRgb(42, 45, 54)), 1);
    private static readonly IBrush MarkerBrush = new SolidColorBrush(Color.FromRgb(255, 210, 80));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromRgb(255, 230, 150));
    private static readonly IBrush LabelBackground = new SolidColorBrush(Color.FromArgb(200, 35, 38, 46));
    private static readonly IPen PlayheadPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 210, 80)), 1);
    private static readonly Typeface LabelTypeface = new("Inter");

    /// <summary>渲染时记录每个 marker 的命中区域（左上角 + 文本宽度），供命中测试使用。</summary>
    private readonly List<(TempoMarker Marker, Rect HitRect)> _hitAreas = new();

    private readonly TextBox _editor;
    private TempoMarker? _editingMarker;
    private Rect _editorRect;
    private bool _commitInProgress;

    static TempoStrip()
    {
        AffectsRender<TempoStrip>(TemposProperty, DurationProperty, PlayheadProperty);
    }

    public TempoStrip()
    {
        ClipToBounds = true;
        Focusable = true;

        _editor = new TextBox
        {
            FontSize = 11,
            Padding = new Thickness(4, 0),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(255, 210, 80)),
            Background = Brushes.White,
            Foreground = Brushes.Black,
            CaretBrush = Brushes.Black,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            IsVisible = false,
        };
        _editor.KeyDown += OnEditorKeyDown;
        _editor.LostFocus += OnEditorLostFocus;

        // Control 不像 Panel 那样有 Children 集合，但我们需要把 TextBox 同时挂到逻辑树和视觉树上：
        // 前者保证它能继承 DataContext / 受到 Style 选择器作用，后者负责实际绘制和命中测试。
        LogicalChildren.Add(_editor);
        VisualChildren.Add(_editor);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _editor.Measure(availableSize);
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_editor.IsVisible)
            _editor.Arrange(_editorRect);
        else
            _editor.Arrange(new Rect(0, 0, 0, 0));
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        double w = bounds.Width;
        double h = bounds.Height;
        if (w <= 0 || h <= 0)
        {
            _hitAreas.Clear();
            return;
        }

        context.FillRectangle(BackgroundBrush, new Rect(0, 0, w, h));
        context.DrawLine(BorderPen, new Point(0, h - 0.5), new Point(w, h - 0.5));

        _hitAreas.Clear();

        var tempos = Tempos;
        if (tempos == null || tempos.Count == 0) return;

        double dur = Math.Max(0.001, Duration);
        double xScale = w / dur;

        // 同 TrackStrip 的策略：竖线总会画；文本如果会和上一个挤一起就跳过，避免一坨字糊在一起。
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

            bool drawText = true;
            if (tx < lastTextRight + labelGap) drawText = false;
            if (tx + tw > w) drawText = false;

            if (drawText)
            {
                var labelRect = new Rect(tx, ty - 1, tw, ft.Height + 2);
                context.FillRectangle(LabelBackground, labelRect);
                context.DrawText(ft, new Point(tx + 4, ty));
                lastTextRight = tx + tw;
                _hitAreas.Add((m, labelRect));
            }
            else
            {
                // 即使文本被合并丢掉，仍然给一个紧贴竖线的小命中区，让用户也能点到
                _hitAreas.Add((m, new Rect(x - 3, 0, 6, h)));
            }
        }

        // 播放头
        if (Playhead > 0 && Playhead <= dur)
        {
            double px = Playhead * xScale;
            context.DrawLine(PlayheadPen, new Point(px, 0), new Point(px, h));
        }
    }

    // ===== 命中编辑 =====

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Tempos == null || Duration <= 0) return;
        // 编辑器自身的点击会冒泡上来，避免点击 TextBox 时把它自己关掉
        if (_editor.IsVisible && _editorRect.Contains(e.GetPosition(this))) return;

        var pt = e.GetPosition(this);

        // 命中区采取"先精确（标签矩形）后放宽（最近 marker 容差）"的两段式策略。
        TempoMarker? hit = null;
        foreach (var (m, rect) in _hitAreas)
        {
            if (rect.Contains(pt))
            {
                hit = m;
                break;
            }
        }
        if (hit == null)
        {
            // 没击中标签时再做最近邻 + 容差判断
            double w = Bounds.Width;
            double xScale = w / Math.Max(0.001, Duration);
            double bestDist = double.MaxValue;
            foreach (var m in Tempos)
            {
                double mx = m.Time * xScale;
                double d = Math.Abs(pt.X - mx);
                if (d < bestDist)
                {
                    bestDist = d;
                    hit = m;
                }
            }
            if (bestDist > 8) hit = null;
        }

        if (hit != null)
        {
            StartEdit(hit);
            e.Handled = true;
        }
    }

    private void StartEdit(TempoMarker marker)
    {
        if (Duration <= 0) return;
        // 已经在编辑同一个：不重置文本，避免吞掉用户已经键入的内容
        if (_editingMarker == marker && _editor.IsVisible) return;

        _editingMarker = marker;
        _editor.Text = marker.Bpm.ToString("F2", CultureInfo.InvariantCulture);

        double w = Bounds.Width;
        double h = Bounds.Height;
        double xScale = w / Math.Max(0.001, Duration);
        double mx = marker.Time * xScale;
        const double editorW = 64;
        const double editorH = 20;
        double left = Math.Clamp(mx + 1, 0, Math.Max(0, w - editorW));
        double top = Math.Max(0, (h - editorH) / 2);
        _editorRect = new Rect(left, top, editorW, editorH);

        _editor.IsVisible = true;
        InvalidateArrange();

        // 等待这一帧的 Arrange 走完后再 Focus + 全选，否则 TextBox 可能还没量好
        Dispatcher.UIThread.Post(() =>
        {
            _editor.Focus();
            _editor.SelectAll();
        }, DispatcherPriority.Background);
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitEdit(saveValue: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CommitEdit(saveValue: false);
            e.Handled = true;
        }
    }

    private void OnEditorLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // 失焦默认提交，模拟 TuneLab 的"点别处即提交"的体验
        CommitEdit(saveValue: true);
    }

    private void CommitEdit(bool saveValue)
    {
        if (_commitInProgress) return;
        _commitInProgress = true;
        try
        {
            var marker = _editingMarker;
            _editingMarker = null;
            _editor.IsVisible = false;
            InvalidateArrange();
            if (!saveValue || marker == null) return;

            if (double.TryParse(_editor.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var bpm)
                && bpm > 0)
            {
                BpmChangeRequested?.Invoke(marker, bpm);
            }
        }
        finally
        {
            _commitInProgress = false;
        }
    }
}
