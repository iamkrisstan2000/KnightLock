using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace KnightLock.Controls;

/// <summary>
/// WinUI 3 style indeterminate ProgressRing: a rotating 270° arc with rounded caps over a
/// faint track. Drawn directly with <see cref="DrawingContext"/> so it stays crisp at any DPI
/// and costs nothing when idle.
/// </summary>
public sealed class ProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(ProgressRing),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender, OnActiveChanged));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(ProgressRing),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, OnActiveChanged));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ArcLengthProperty = DependencyProperty.Register(
        nameof(ArcLength), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(280.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public double ArcLength
    {
        get => (double)GetValue(ArcLengthProperty);
        set => SetValue(ArcLengthProperty, value);
    }

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private double _angle;
    private bool _renderingHooked;

    public ProgressRing()
    {
        Width = 40;
        Height = 40;
        SnapsToDevicePixels = true;
        Loaded += (_, _) => UpdateRenderHook();
        Unloaded += (_, _) => UpdateRenderHook();
        IsVisibleChanged += (_, _) => UpdateRenderHook();
    }

    private static void OnActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ProgressRing)d).UpdateRenderHook();

    private void UpdateRenderHook()
    {
        bool shouldRender = IsActive && IsVisible && IsLoaded;
        if (shouldRender && !_renderingHooked)
        {
            CompositionTarget.Rendering += OnRendering;
            _renderingHooked = true;
        }
        else if (!shouldRender && _renderingHooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderingHooked = false;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double t = Clock.Elapsed.TotalSeconds;
        _angle = (t * 320.0) % 360.0;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsNaN(Width) ? 40 : Width;
        double h = double.IsNaN(Height) ? 40 : Height;
        return new Size(w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(RenderSize.Width, RenderSize.Height);
        if (size <= 2 || double.IsNaN(size)) return;

        double thickness = Math.Max(1.0, StrokeThickness);
        double radius = (size - thickness) / 2.0;

        var center = new Point(RenderSize.Width / 2, RenderSize.Height / 2);

        // Faint track
        var trackPen = new Pen(WithOpacity(Foreground, 0.18), thickness);
        trackPen.Freeze();
        dc.DrawEllipse(null, trackPen, center, radius, radius);

        // Rotating arc with rounded caps
        var arcPen = new Pen(Foreground, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        arcPen.Freeze();

        double sweep = Math.Clamp(ArcLength, 10, 359.9);
        double startAngle = _angle;

        Point start = PointOnCircle(center, radius, startAngle);
        Point end = PointOnCircle(center, radius, startAngle + sweep);

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(radius, radius), 0, sweep > 180,
                      SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();

        dc.DrawGeometry(null, arcPen, geometry);
    }

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        double rad = (angleDegrees - 90) * Math.PI / 180.0;
        return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
    }

    private static Brush WithOpacity(Brush source, double opacity)
    {
        if (source is SolidColorBrush solid)
        {
            var brush = new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), solid.Color.R, solid.Color.G, solid.Color.B));
            brush.Freeze();
            return brush;
        }
        var clone = source.Clone();
        clone.Opacity = opacity;
        clone.Freeze();
        return clone;
    }
}
