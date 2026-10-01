using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PerfPanel.Controls;

/// <summary>圆环用量表:背景圆环 + 自顶部顺时针的进度弧 + 中心文字。</summary>
public class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(RingGauge),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x1B, 0x27, 0x43)),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextFontSizeProperty = DependencyProperty.Register(
        nameof(TextFontSize), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(18.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingThicknessProperty = DependencyProperty.Register(
        nameof(RingThickness), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>用量 0-100。</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Brush RingBrush
    {
        get => (Brush)GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush TextBrush
    {
        get => (Brush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public double TextFontSize
    {
        get => (double)GetValue(TextFontSizeProperty);
        set => SetValue(TextFontSizeProperty, value);
    }

    public double RingThickness
    {
        get => (double)GetValue(RingThicknessProperty);
        set => SetValue(RingThicknessProperty, value);
    }

    private static readonly Typeface TextTypeface = new(new FontFamily("Consolas"), FontStyles.Normal,
        FontWeights.Bold, FontStretches.Normal);

    protected override void OnRender(DrawingContext dc)
    {
        double side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 0) return;
        double th = Math.Min(RingThickness, side / 4);
        double r = (side - th) / 2 - 1;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);

        var trackPen = new Pen(TrackBrush, th);
        dc.DrawEllipse(null, trackPen, center, r, r);

        double pct = Math.Clamp(Value, 0, 100);
        if (pct > 0.5)
        {
            double sweep = pct / 100.0 * 2 * Math.PI;
            var start = new Point(center.X, center.Y - r);
            var end = new Point(center.X + r * Math.Sin(sweep), center.Y - r * Math.Cos(sweep));
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(start, false, false);
                ctx.ArcTo(end, new Size(r, r), 0, pct > 50, SweepDirection.Clockwise, true, false);
            }
            var valuePen = new Pen(RingBrush, th) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, valuePen, geo);
        }

        string text = Text ?? "";
        if (text.Length == 0) return;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            TextTypeface, TextFontSize, TextBrush, dip);
        dc.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
    }
}
