using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;

namespace PerfPanel.Controls;

/// <summary>轻量历史曲线:StreamGeometry 自绘,无第三方图表依赖。120 点/条。</summary>
public class HistoryGraph : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(float[]), typeof(HistoryGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineColorProperty = DependencyProperty.Register(
        nameof(LineColor), typeof(Color), typeof(HistoryGraph),
        new FrameworkPropertyMetadata(Color.FromRgb(0x22, 0xD3, 0xEE), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>true 时按数据自适应纵轴,否则用固定的 Min/Max(负载类固定 0-100)。</summary>
    public static readonly DependencyProperty AutoScaleProperty = DependencyProperty.Register(
        nameof(AutoScale), typeof(bool), typeof(HistoryGraph),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public float[]? Values
    {
        get => (float[]?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Color LineColor
    {
        get => (Color)GetValue(LineColorProperty);
        set => SetValue(LineColorProperty, value);
    }

    public bool AutoScale
    {
        get => (bool)GetValue(AutoScaleProperty);
        set => SetValue(AutoScaleProperty, value);
    }

    public float Min { get; set; } = 0f;
    public float Max { get; set; } = 100f;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // 背景网格:两横线 + 底线
        var gridBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0x9D, 0xB8, 0xE8));
        var gridPen = new Pen(gridBrush, 1) { DashStyle = new DashStyle([2, 4], 0) };
        for (int i = 1; i <= 2; i++)
        {
            double y = h * i / 3;
            dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
        }
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0x22, 0xD3, 0xEE)), 1),
            new Point(0, h - 1), new Point(w, h - 1));

        var vals = Values;
        if (vals == null || vals.Length < 2) return;

        double min = Min, max = Max;
        if (AutoScale)
        {
            min = 0;
            max = Math.Max(vals.Max(), 1f);
            max *= 1.15; // 顶部留白
        }
        double range = max - min;
        if (range <= 0) range = 1;

        double stepX = w / (vals.Length - 1);
        var points = new Point[vals.Length];
        for (int i = 0; i < vals.Length; i++)
        {
            double v = Math.Clamp(vals[i], min, max);
            points[i] = new Point(i * stepX, h - (v - min) / range * h);
        }

        // 填充
        var fillGeo = new StreamGeometry();
        using (var ctx = fillGeo.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            for (int i = 1; i < points.Length; i++) ctx.LineTo(points[i], true, false);
            ctx.LineTo(new Point(w, h), true, false);
            ctx.LineTo(new Point(0, h), true, false);
        }
        fillGeo.Freeze();
        var gradFill = new LinearGradientBrush(
            Color.FromArgb(0x50, LineColor.R, LineColor.G, LineColor.B),
            Color.FromArgb(0x00, LineColor.R, LineColor.G, LineColor.B), 90);
        gradFill.Freeze();
        dc.DrawGeometry(gradFill, null, fillGeo);

        // 折线
        var lineGeo = new StreamGeometry();
        using (var ctx = lineGeo.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            for (int i = 1; i < points.Length; i++) ctx.LineTo(points[i], true, false);
        }
        lineGeo.Freeze();
        var lineBrush = new SolidColorBrush(LineColor);
        lineBrush.Freeze();
        dc.DrawGeometry(null, new Pen(lineBrush, 1.8) { LineJoin = PenLineJoin.Round }, lineGeo);

        // 末端亮点
        var last = points[^1];
        dc.DrawEllipse(lineBrush, null, last, 2.6, 2.6);
    }
}
