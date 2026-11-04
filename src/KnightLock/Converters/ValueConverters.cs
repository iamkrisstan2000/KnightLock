using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace KnightLock.Converters;

/// <summary>
/// Human-readable byte sizes ("1.4 GB") for vault cards, progress lines and dialog copy. Static
/// rather than an IValueConverter: every call site is C#, so the converter plumbing was dead weight.
/// </summary>
public static class ByteSizeConverter
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string Format(long bytes)
    {
        if (bytes <= 0) return "0 B";

        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes:N0} B"
            : $"{size:0.#} {Units[unit]}";
    }
}

/// <summary>
/// Builds a clip geometry for a surface whose only rounded corner is the top-left one
/// (width, height, radius in). Used by the content well: relying on Border.CornerRadius alone
/// let the corner square off in Acrylic, where DWM composites the backdrop behind the window.
/// </summary>
public sealed class RoundedTopLeftClipConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2) return Binding.DoNothing;
        if (values[0] is not double width || values[1] is not double height) return Binding.DoNothing;
        if (width <= 0 || height <= 0) return Binding.DoNothing;

        double radius = 10;
        if (parameter is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double custom))
            radius = custom;

        radius = Math.Min(radius, Math.Min(width, height) / 2);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(0, radius), isFilled: true, isClosed: true);
            ctx.ArcTo(new Point(radius, 0), new Size(radius, radius), 0,
                      isLargeArc: false, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
            ctx.LineTo(new Point(width, 0), true, false);
            ctx.LineTo(new Point(width, height), true, false);
            ctx.LineTo(new Point(0, height), true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Geometry shared by the content well and the notch filler that sits behind its rounded
/// top-left corner.
///
/// The well's corner is a cut-out: the clip keeps the arc and removes the sliver outside it.
/// Nothing else paints in that sliver, so whatever is behind the window shows through it — a
/// dark DWM surface over Mica and Solid (which reads as a fillet and was signed off), but the
/// wallpaper itself over Acrylic, where it showed up as a bright spur brighter than either the
/// rail or the well. Sealing the sliver with the chrome brush makes the fillet read as shell
/// surface in every material.
/// </summary>
public static class WellGeometry
{
    /// <summary>Corner radius of the well's rounded top-left corner, in DIPs.</summary>
    public const double CornerRadius = 10;

    /// <summary>
    /// The sliver the clip cuts away: the corner square minus the quarter disc the clip keeps.
    /// Built as a combined geometry so it is the exact complement of the clip's arc by
    /// construction, rather than a hand-traced arc that could drift out of phase with it.
    /// </summary>
    public static Geometry CornerNotch { get; } = CreateCornerNotch();

    private static Geometry CreateCornerNotch()
    {
        double radius = CornerRadius;

        var geometry = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, radius, radius)),
            new EllipseGeometry(new Point(radius, radius), radius, radius));

        geometry.Freeze();
        return geometry;
    }
}
