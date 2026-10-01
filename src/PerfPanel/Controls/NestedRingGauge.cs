using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PerfPanel.Controls;

/// <summary>一条环:用量百分比 + 颜色(嵌套圆环的一层)。</summary>
public sealed record RingSlice(double UsedPercent, Brush Brush);

/// <summary>嵌套圆环用量表:多条环由外到内依次排列(如 5h/周/月),每条含暗色轨道与自顶部顺时针的用量弧。</summary>
public class NestedRingGauge : FrameworkElement
{
    public static readonly DependencyProperty RingsProperty = DependencyProperty.Register(
        nameof(Rings), typeof(IReadOnlyList<RingSlice>), typeof(NestedRingGauge),
        new FrameworkPropertyMetadata(Array.Empty<RingSlice>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(NestedRingGauge),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x1B, 0x27, 0x43)),
            FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>由外到内的环(第一个 = 最外圈)。</summary>
    public IReadOnlyList<RingSlice> Rings
    {
        get => (IReadOnlyList<RingSlice>)GetValue(RingsProperty);
        set => SetValue(RingsProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var rings = Rings;
        int n = rings.Count;
        if (n == 0) return;

        double side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 0) return;
        double outerR = side / 2 - 1;
        double band = outerR / n;

        for (int i = 0; i < n; i++)
        {
            double r = outerR - (i + 0.5) * band;
            double th = Math.Max(2, band - 2.5);

            var trackPen = new Pen(TrackBrush, th);
            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            dc.DrawEllipse(null, trackPen, center, r, r);

            double pct = Math.Clamp(rings[i].UsedPercent, 0, 100);
            if (pct <= 0.5) continue;
            double sweep = pct / 100.0 * 2 * Math.PI;
            var start = new Point(center.X, center.Y - r);
            var end = new Point(center.X + r * Math.Sin(sweep), center.Y - r * Math.Cos(sweep));
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(start, false, false);
                ctx.ArcTo(end, new Size(r, r), 0, pct > 50, SweepDirection.Clockwise, true, false);
            }
            var valuePen = new Pen(rings[i].Brush, th)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            dc.DrawGeometry(null, valuePen, geo);
        }
    }
}
