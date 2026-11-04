using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace KnightLock.Interop;

public enum BackdropKind
{
    /// <summary>No system material — the app paints its own Fluent surface.</summary>
    None = 0,
    /// <summary>Windows 11 Mica (desktop-tinted, most subtle).</summary>
    Mica = 2,
    /// <summary>Windows 11 Acrylic (blurred wallpaper).</summary>
    Acrylic = 3,
    /// <summary>Windows 11 Tabbed host backdrop.</summary>
    Tabbed = 4
}

/// <summary>
/// Thin, dependency-free wrapper over the DWM / composition APIs that give a WPF window
/// real Windows 11 materials: Mica &amp; Acrylic backdrops, rounded corners, native dark
/// title bar, plus system theme + accent colour discovery.
/// </summary>
public static class WindowsInterop
{
    // ---- dwmapi -----------------------------------------------------------
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWA_MICA_EFFECT = 1029;   // Windows 11 21H2 fallback

    private const int DWMWCP_ROUND = 2;
    private const int DWMWCP_DONOTROUND = 1;
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // ---- user32 (Windows 10 fallback acrylic) -----------------------------
    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_BLURBEHIND = 3;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
    private const int ACCENT_DISABLED = 0;

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    // ---- maximized bounds (user32) ----------------------------------------
    // A WindowStyle=None window is maximized to the monitor rectangle by default, so it slides
    // under the taskbar. These structs let the window answer WM_GETMINMAXINFO with the work area.
    public const int MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    // ---- system info ------------------------------------------------------
    public static int WindowsBuild
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key?.GetValue("CurrentBuildNumber") is string s && int.TryParse(s, out int build))
                    return build;
                if (key?.GetValue("CurrentBuild") is string s2 && int.TryParse(s2, out int build2))
                    return build2;
            }
            catch { }
            return Environment.OSVersion.Version.Build;
        }
    }

    public static bool IsWindows11 => WindowsBuild >= 22000;

    /// <summary>True when the OS composes real Mica/Acrylic for us.</summary>
    public static bool SupportsSystemMaterial => WindowsBuild >= 17763; // 1809

    /// <summary>
    /// True when the material re-tints itself after a light/dark switch, so re-applying the
    /// backdrop on a theme change would be pointless work — and a visible teardown of the material.
    /// Windows 11 backdrops follow the window's immersive dark mode; the Windows 10 acrylic path
    /// writes its tint into the accent policy and has to be rebuilt.
    /// </summary>
    public static bool BackdropTintFollowsTheme => IsWindows11;

    // ---- backdrop ---------------------------------------------------------
    /// <summary>
    /// Applies the requested material. Returns false when the OS cannot provide it, so the
    /// caller can fall back to a painted Fluent surface instead of showing a black window.
    /// </summary>
    public static bool TryApplyBackdrop(IntPtr hwnd, BackdropKind kind, bool isDark)
    {
        if (hwnd == IntPtr.Zero) return false;

        if (kind == BackdropKind.None)
        {
            DisableComposition(hwnd);
            return false;
        }

        if (IsWindows11 && WindowsBuild >= 22621)
        {
            int value = (int)kind;
            if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref value, sizeof(int)) == 0)
                return true;
        }

        if (IsWindows11)
        {
            // 21H2 / 22H1 only expose the undocumented Mica toggle.
            int mica = kind == BackdropKind.Mica ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DWMWA_MICA_EFFECT, ref mica, sizeof(int)) == 0 && mica == 1)
                return true;
        }

        // Windows 10 1809+ : Acrylic via the composition attribute.
        return TryApplyLegacyAcrylic(hwnd, isDark, kind == BackdropKind.Mica);
    }

    private static bool TryApplyLegacyAcrylic(IntPtr hwnd, bool isDark, bool preferSubtle)
    {
        try
        {
            // Tint: near-black in dark mode, near-white in light mode, low alpha.
            int tint = isDark
                ? unchecked((int)0x99202020)   // AABBGGRR
                : unchecked((int)0x99F3F3F3);

            var policy = new AccentPolicy
            {
                AccentState = preferSubtle ? ACCENT_ENABLE_BLURBEHIND : ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags = 0x20,               // draw all borders
                GradientColor = tint,
                AnimationId = 0
            };

            int size = Marshal.SizeOf<AccentPolicy>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, ptr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WCA_ACCENT_POLICY,
                    Data = ptr,
                    SizeOfData = size
                };
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch
        {
            return false;
        }
    }

    private static void DisableComposition(IntPtr hwnd)
    {
        try
        {
            var policy = new AccentPolicy { AccentState = ACCENT_DISABLED };
            int size = Marshal.SizeOf<AccentPolicy>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, ptr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WCA_ACCENT_POLICY,
                    Data = ptr,
                    SizeOfData = size
                };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        catch { }
    }

    // ---- window chrome ----------------------------------------------------
    public static void SetDarkTitleBar(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return;
        int value = dark ? 1 : 0;
        // 19 on older 1809 builds, 20 from 20H1 onward; try both.
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref value, sizeof(int));
    }

    public static void SetRoundedCorners(IntPtr hwnd, bool rounded)
    {
        if (hwnd == IntPtr.Zero || !IsWindows11) return;
        int value = rounded ? DWMWCP_ROUND : DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref value, sizeof(int));
    }

    /// <summary>Removes the 1px DWM border so the window melts into the Mica material.</summary>
    public static void RemoveWindowBorder(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindows11) return;
        int none = unchecked((int)DWMWA_COLOR_NONE);
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));
    }

    // ---- system theme & accent -------------------------------------------
    public static bool IsSystemThemeDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v)
                return v == 0;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Reads the user's Windows accent colour (DWM\AccentColor, stored as ABGR).
    /// Returns null when it cannot be read.
    /// </summary>
    public static Color? GetSystemAccentColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                byte r = (byte)(abgr & 0xFF);
                byte g = (byte)((abgr >> 8) & 0xFF);
                byte b = (byte)((abgr >> 16) & 0xFF);
                return Color.FromRgb(r, g, b);
            }
        }
        catch { }
        return null;
    }

    /// <summary>Chooses black or white for text drawn on top of the supplied colour.</summary>
    public static Color ContrastForeground(Color background)
    {
        double luminance = (0.2126 * Channel(background.R) + 0.7152 * Channel(background.G) + 0.0722 * Channel(background.B));
        return luminance > 0.45 ? Color.FromRgb(0x00, 0x00, 0x00) : Colors.White;

        static double Channel(byte c)
        {
            double s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
    }

    /// <summary>Blend two colours (used to derive hover/pressed accent shades).</summary>
    public static Color Blend(Color from, Color to, double amount)
    {
        byte Mix(byte a, byte b) => (byte)Math.Clamp(a + (b - a) * amount, 0, 255);
        return Color.FromArgb(Mix(from.A, to.A), Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
    }
}

/// <summary>
/// Window-level helper that keeps the material in sync with the app theme.
///
/// Every call here is a DWM attribute write, and DWM answers each one by repainting the
/// non-client frame. A write is therefore *not* free just because the value is unchanged:
/// re-declaring the system backdrop makes DWM tear the material down and build it again, and for
/// the frame in between the window is drawn without it, with the frame's own border and rounded
/// corner standing in — which is what showed up as a split-second flicker of the window edge when
/// toggling dark and light. So this class remembers what it has already written and only talks to
/// DWM when something genuinely changed.
/// </summary>
public static class WindowMaterial
{
    public static bool MaterialApplied { get; private set; }

    private static IntPtr _hwnd;
    private static bool _chromeApplied;
    private static bool _backdropApplied;
    private static BackdropKind _appliedKind;
    private static bool _appliedDark;

    public static bool Apply(Window window, BackdropKind kind, bool isDark)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;

        // A new window handle means all of this must be stated again from scratch.
        if (hwnd != _hwnd)
        {
            _hwnd = hwnd;
            _chromeApplied = false;
            _backdropApplied = false;
        }

        if (!_chromeApplied)
        {
            WindowsInterop.SetRoundedCorners(hwnd, true);
            WindowsInterop.RemoveWindowBorder(hwnd);
            _chromeApplied = true;
        }

        if (!_backdropApplied || isDark != _appliedDark)
            WindowsInterop.SetDarkTitleBar(hwnd, isDark);

        // On Windows 11 the system backdrop follows the window's immersive dark mode by itself, so
        // a theme switch needs nothing more than the attribute above. Only the material *kind*
        // requires a re-apply there. The Windows 10 acrylic path bakes its tint into the accent
        // policy, so that one does have to be rebuilt when the theme flips.
        bool backdropChanged = !_backdropApplied
            || kind != _appliedKind
            || (!WindowsInterop.BackdropTintFollowsTheme && isDark != _appliedDark);

        if (backdropChanged)
            MaterialApplied = WindowsInterop.TryApplyBackdrop(hwnd, kind, isDark);

        _appliedKind = kind;
        _appliedDark = isDark;
        _backdropApplied = true;

        // The window must be transparent for the system material to show through.
        Brush surface = MaterialApplied
            ? Brushes.Transparent
            : (Brush)Application.Current.FindResource("ShellBackdropBrush");

        if (!ReferenceEquals(window.Background, surface))
            window.Background = surface;

        return MaterialApplied;
    }
}
