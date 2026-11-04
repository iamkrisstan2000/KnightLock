using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KnightLock.Controls;

/// <summary>
/// Read-only, single-line path display used wherever KnightLock shows a location it has already
/// resolved (source folder, container destination).
///
/// Renders as a field-shaped surface, ellipsizes long paths, exposes the full path as a tooltip,
/// and falls back to a muted placeholder when nothing has been chosen yet. Colours are attached
/// with SetResourceReference so the field follows a live light/dark switch.
/// </summary>
public partial class PathField : UserControl
{
    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(
        nameof(Path), typeof(string), typeof(PathField),
        new PropertyMetadata(string.Empty, OnPathChanged));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(PathField),
        new PropertyMetadata("Nothing selected yet", OnPathChanged));

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(Geometry), typeof(PathField),
        new PropertyMetadata(null, OnIconChanged));

    public PathField()
    {
        InitializeComponent();

        MouseEnter += (_, _) => Chrome.SetResourceReference(
            Border.BorderBrushProperty, "ControlStrokeColorSecondaryBrush");
        MouseLeave += (_, _) => Chrome.SetResourceReference(
            Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");

        Apply();
    }

    /// <summary>Full path shown in the field. Empty means "nothing selected".</summary>
    public string Path
    {
        get => (string)GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    /// <summary>Muted text shown while <see cref="Path"/> is empty.</summary>
    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>Leading glyph; defaults to a folder.</summary>
    public Geometry? Icon
    {
        get => (Geometry?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>True when a value is present.</summary>
    public bool HasPath => !string.IsNullOrWhiteSpace(Path);

    private static void OnPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => (d as PathField)?.Apply();

    private static void OnIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PathField field && e.NewValue is Geometry geometry)
            field.Glyph.Data = geometry;
    }

    private void Apply()
    {
        if (Value is null) return;

        bool hasPath = HasPath;

        Value.Text = hasPath ? Path : Placeholder;
        Value.SetResourceReference(
            TextBlock.ForegroundProperty,
            hasPath ? "TextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush");

        ToolTip = hasPath ? Path : null;
    }
}
