using System;
using System.Windows;
using System.Windows.Media;
using KnightLock.Interop;

namespace KnightLock.Services;

public enum AppTheme
{
    Light,
    Dark
}

/// <summary>
/// Swaps the Fluent palette at runtime and keeps the window material in sync.
/// Light/Dark can follow Windows, or be pinned by the user in Settings.
/// </summary>
public static class ThemeService
{
    public static AppTheme Current { get; private set; } = AppTheme.Light;
    public static bool IsDark => Current == AppTheme.Dark;

    public static event EventHandler? ThemeChanged;

    public static AppTheme DetectSystemTheme() =>
        WindowsInterop.IsSystemThemeDark() ? AppTheme.Dark : AppTheme.Light;

    public static AppTheme Resolve(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => AppTheme.Light,
        ThemePreference.Dark => AppTheme.Dark,
        _ => DetectSystemTheme()
    };

    public static void Apply(AppTheme theme, bool useSystemAccent = true)
    {
        if (Application.Current is null) return;

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var source = new Uri(
            theme == AppTheme.Dark
                ? "pack://application:,,,/Themes/Palette.Dark.xaml"
                : "pack://application:,,,/Themes/Palette.Light.xaml",
            UriKind.Absolute);

        int index = -1;
        for (int i = 0; i < dictionaries.Count; i++)
        {
            string? src = dictionaries[i].Source?.OriginalString;
            if (src is not null && src.Contains("Palette.", StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        var palette = new ResourceDictionary { Source = source };

        if (useSystemAccent)
            ApplySystemAccent(palette, theme);

        if (index >= 0) dictionaries[index] = palette;
        else dictionaries.Insert(0, palette);

        Current = theme;
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static void ApplySystemAccent(ResourceDictionary palette, AppTheme theme)
    {
        Color? maybeAccent = WindowsInterop.GetSystemAccentColor();
        if (maybeAccent is null) return;

        Color accent = maybeAccent.Value;
        if (accent.A == 0) return;

        // Dark surfaces need a brighter accent to keep AA contrast.
        if (theme == AppTheme.Dark)
            accent = WindowsInterop.Blend(accent, Colors.White, 0.32);

        Color onAccent = WindowsInterop.ContrastForeground(accent);
        Color accentText = theme == AppTheme.Dark
            ? WindowsInterop.Blend(accent, Colors.White, 0.45)
            : WindowsInterop.Blend(accent, Colors.Black, 0.18);

        void Put(string key, Brush brush) => palette[key] = brush;

        palette["AccentColor"] = accent;
        Put("AccentFillColorDefaultBrush", Solid(accent, 1.0));
        Put("AccentFillColorSecondaryBrush", Solid(accent, 0.90));
                Put("AccentTextFillColorPrimaryBrush", Solid(accentText, 1.0));
        Put("TextOnAccentFillColorPrimaryBrush", Solid(onAccent, 1.0));
                Put("BrandTintBrush", Solid(accent, 0.10));

        static SolidColorBrush Solid(Color c, double alpha)
        {
            var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), c.R, c.G, c.B));
            brush.Freeze();
            return brush;
        }
    }
}
