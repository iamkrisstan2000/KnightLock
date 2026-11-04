using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace KnightLock.Controls;

/// <summary>
/// Draws a Fluent-style vector icon from a 24x24 path geometry, scaled crisply to any size.
///
/// Why not an icon font? Segoe Fluent Icons is not guaranteed to be present (and fails
/// silently to tofu boxes when it isn't), so KnightLock ships its own hand-drawn geometry.
/// The foreground inherits <see cref="TextElement.ForegroundProperty"/>, so an icon picks up
/// the surrounding text colour — including hover/pressed states inside button templates.
/// </summary>
public sealed class IconPresenter : FrameworkElement
{
    private const double Grid = 24.0;

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(IconPresenter),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner(typeof(IconPresenter),
            new FrameworkPropertyMetadata(Brushes.Black,
                FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Icon geometry, authored on a 24x24 grid.</summary>
    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IconPresenter()
    {
        Width = 16;
        Height = 16;
        SnapsToDevicePixels = false;
        IsHitTestVisible = false;
        RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsNaN(Width) ? Grid : Width;
        double h = double.IsNaN(Height) ? Grid : Height;
        return new Size(w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        Geometry? geometry = Data;
        if (geometry is null) return;

        double w = RenderSize.Width;
        double h = RenderSize.Height;
        if (w <= 0 || h <= 0) return;

        double scale = Math.Min(w / Grid, h / Grid);
        double offsetX = (w - Grid * scale) / 2.0;
        double offsetY = (h - Grid * scale) / 2.0;

        dc.PushTransform(new TranslateTransform(offsetX, offsetY));
        dc.PushTransform(new ScaleTransform(scale, scale));

        var brush = Foreground;
        if (brush.CanFreeze) brush.Freeze();

        dc.DrawGeometry(brush, null, geometry);

        dc.Pop();
        dc.Pop();
    }
}
