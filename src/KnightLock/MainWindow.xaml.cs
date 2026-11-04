using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using KnightLock.Controls;
using KnightLock.Core;
using KnightLock.Interop;
using KnightLock.Services;
using KnightLock.Views;

namespace KnightLock;

public partial class MainWindow : Window
{
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_DWMCOMPOSITIONCHANGED = 0x031E;
    private const int WM_GETMINMAXINFO = 0x0024;

    private readonly MyVaultsPage _vaultsPage;
    private readonly CreateVaultPage _createPage;
    private readonly SettingsPage _settingsPage;

    private HwndSource? _hwndSource;
    private bool _navCollapsed = true;
    private bool _shutdownReady;

    public MainWindow()
    {
        InitializeComponent();

        App.Dialogs = DialogLayer;

        var settings = App.Current.Settings;
        Width = settings.WindowWidth;
        Height = settings.WindowHeight;

        _vaultsPage = new MyVaultsPage();
        _createPage = new CreateVaultPage();
        _settingsPage = new SettingsPage();

        PageHost.Content = _vaultsPage;

        VersionText.Text = $"v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"} · AES-256-GCM";

        ApplyNavState();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
        Closing += OnClosing;

        // Switching away is the cheapest moment to give the heap back: nothing is animating and the
        // user is looking at another window.
        Deactivated += (_, _) => MemoryTrimmer.Release();

        ThemeService.ThemeChanged += (_, _) => Dispatcher.Invoke(ApplyThemeToWindow);
        App.Current.Sessions.SessionsChanged += (_, _) => Dispatcher.Invoke(UpdateSessionChrome);
    }

    // ------------------------------------------------------------------ lifetime

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);

        ApplyThemeToWindow();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateSessionChrome();
        _vaultsPage.Refresh();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_GETMINMAXINFO:
                // Handled here rather than by the framework: a WindowStyle=None window would be
                // maximized to the full monitor rectangle and bury the taskbar. Answering with the
                // monitor's work area keeps the taskbar visible and multi-monitor correct.
                ApplyMaximizedBounds(hwnd, lParam);
                handled = true;
                break;

            case WM_SETTINGCHANGE:
            case WM_DWMCOMPOSITIONCHANGED:
                // Windows theme or accent colour changed while we are running.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (App.Current.Settings.NormalizedTheme == ThemePreference.System)
                        ThemeService.Apply(ThemeService.DetectSystemTheme(), App.Current.Settings.UseSystemAccent);
                    else
                        ApplyThemeToWindow();
                }));
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Constrains maximize/restore geometry to the work area of the monitor the window is on, and
    /// reports the window's MinWidth/MinHeight as the minimum tracking size.
    /// </summary>
    private void ApplyMaximizedBounds(IntPtr hwnd, IntPtr lParam)
    {
        if (Marshal.PtrToStructure(lParam, typeof(WindowsInterop.MINMAXINFO))
            is not WindowsInterop.MINMAXINFO info)
            return;

        IntPtr monitor = WindowsInterop.MonitorFromWindow(hwnd, WindowsInterop.MONITOR_DEFAULTTONEAREST);

        var monitorInfo = new WindowsInterop.MONITORINFO
        {
            cbSize = Marshal.SizeOf(typeof(WindowsInterop.MONITORINFO))
        };

        if (monitor != IntPtr.Zero && WindowsInterop.GetMonitorInfo(monitor, ref monitorInfo))
        {
            WindowsInterop.RECT work = monitorInfo.rcWork;
            WindowsInterop.RECT screen = monitorInfo.rcMonitor;

            info.ptMaxPosition.X = work.Left - screen.Left;
            info.ptMaxPosition.Y = work.Top - screen.Top;
            info.ptMaxSize.X = work.Right - work.Left;
            info.ptMaxSize.Y = work.Bottom - work.Top;
        }

        // The OS enforces min tracking size for frame resizes; WPF's MinWidth/MinHeight are in DIPs.
        double scale = _hwndSource?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        int minWidth = (int)Math.Ceiling((double.IsNaN(MinWidth) ? 0 : MinWidth) * scale);
        int minHeight = (int)Math.Ceiling((double.IsNaN(MinHeight) ? 0 : MinHeight) * scale);

        info.ptMinTrackSize.X = Math.Max(info.ptMinTrackSize.X, minWidth);
        info.ptMinTrackSize.Y = Math.Max(info.ptMinTrackSize.Y, minHeight);

        Marshal.StructureToPtr(info, lParam, fDeleteOld: false);
    }

    private void ApplyThemeToWindow()
    {
        BackdropKind kind = App.Current.Settings.NormalizedBackdrop switch
        {
            BackdropPreference.Acrylic => BackdropKind.Acrylic,
            BackdropPreference.Solid => BackdropKind.None,
            _ => BackdropKind.Mica
        };

        bool material = WindowMaterial.Apply(this, kind, ThemeService.IsDark);

        // When the OS cannot composite a material, paint a solid Fluent surface instead.
        // Acrylic additionally gets a 3% veil: DWM's transient backdrop lets a little too much of
        // a bright wallpaper through at this window size.
        RootGrid.Background = material
            ? (kind == BackdropKind.Acrylic
                ? (Brush)FindResource("AcrylicVeilBrush")
                : Brushes.Transparent)
            : (Brush)FindResource("ShellBackdropBrush");

        // The well's rounded corner is a cut-out, so the sliver outside the arc is a hole onto
        // whatever is behind the window. Over Mica and Solid that backing surface is dark, so the
        // hole reads as a fillet. Over Acrylic it is the wallpaper, and the sliver came out
        // brighter than the rail and the well alike (measured 126 luma against 102 and 105) — a
        // bright spur that looked like a rendering fault. Sealing it with the chrome brush makes
        // the corner read as shell surface. Mica and Solid keep the plain cut-out, unchanged.
        WellCornerNotch.Fill = material && kind == BackdropKind.Acrylic
            ? (Brush)FindResource("NavigationFillBrush")
            : Brushes.Transparent;

        _settingsPage.RefreshBackdropStatus(material, kind);
    }

    /// <summary>Re-applies theme + material (called when Settings changes appearance).</summary>
    public void ReapplyAppearance() => ApplyThemeToWindow();

    // ------------------------------------------------------------------ title bar

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState != MouseButtonState.Pressed) return;

        try
        {
            if (WindowState == WindowState.Maximized)
            {
                // Restore first so dragging from a maximized title bar behaves like Explorer.
                Point position = e.GetPosition(this);
                double ratio = position.X / Math.Max(1, ActualWidth);
                WindowState = WindowState.Normal;
                Left = Math.Max(0, (Left + position.X) - (ActualWidth * ratio));
                Top = Math.Max(0, Top + position.Y - 20);
            }
            DragMove();
        }
        catch (InvalidOperationException) { }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnStateChanged(object? sender, EventArgs e)
    {
        MaximizeGlyph.Data = (Geometry)FindResource(
            WindowState == WindowState.Maximized ? "GeoRestore" : "GeoMaximize");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------------ navigation

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || PageHost is null || _vaultsPage is null) return;

        if (ReferenceEquals(sender, NavCreateVault))
        {
            PageHost.Content = _createPage;
            _createPage.OnNavigatedTo();
        }
        else if (ReferenceEquals(sender, NavSettings))
        {
            PageHost.Content = _settingsPage;
            _settingsPage.OnNavigatedTo();
        }
        else
        {
            PageHost.Content = _vaultsPage;
            _vaultsPage.Refresh();
        }
    }

    public void NavigateToCreateVault(string? sourceFolder = null)
    {
        NavCreateVault.IsChecked = true;
        _createPage.PrefillSource(sourceFolder);
    }

    public void NavigateToVaults() => NavMyVaults.IsChecked = true;

    /// <summary>
    /// Opens a container handed to the app by the shell (.vault double-click / "Open with"):
    /// lands on My Vaults with that vault selected and the password field focused.
    /// </summary>
    public void OpenVaultFromShell(string path)
    {
        NavigateToVaults();
        _vaultsPage.OpenFromShell(path);
    }

    private void ToggleNav_Click(object sender, RoutedEventArgs e)
    {
        _navCollapsed = !_navCollapsed;
        ApplyNavState();
    }

    /// <summary>
    /// Applies the rail &lt;-&gt; labelled pane state.
    ///
    /// Collapsed (default) is the 48 px icon rail from the reference design: hamburger, the two
    /// destinations, then the session lock, a hairline divider and Settings at the foot. Expanding
    /// widens the same pane and reveals labels plus the session card.
    /// </summary>
    private void ApplyNavState()
    {
        double railWidth = _navCollapsed ? 48 : 252;
        Thickness itemMargin = _navCollapsed ? new Thickness(4, 0, 4, 0) : new Thickness(12, 0, 12, 0);

        NavColumn.Width = new GridLength(railWidth);
        NavItemsPanel.Margin = itemMargin;
        NavSettings.Margin = new Thickness(itemMargin.Left, 0, itemMargin.Right, _navCollapsed ? 6 : 12);

        // The left inset stays at 4 in both states: with the button's fixed 40 px width that puts
        // its glyph on the same 24 px axis as the nav items and the collapsed rail, so expanding
        // reveals the labels without shifting the icon column.
        HamburgerButton.Margin = _navCollapsed
            ? new Thickness(4, 6, 4, 2)
            : new Thickness(4, 8, 12, 4);
        HamburgerButton.HorizontalAlignment = _navCollapsed
            ? HorizontalAlignment.Center
            : HorizontalAlignment.Left;
        HamburgerButton.ToolTip = _navCollapsed ? "Expand navigation" : "Collapse navigation";

        // Rail-only chrome vs. expanded-only session card
        RailLockButton.Visibility = _navCollapsed ? Visibility.Visible : Visibility.Collapsed;
        RailDivider.Visibility = _navCollapsed ? Visibility.Visible : Visibility.Collapsed;
        PaneFooter.Visibility = _navCollapsed ? Visibility.Collapsed : Visibility.Visible;

        SetNavLabel(NavMyVaults, "My Vaults");
        SetNavLabel(NavCreateVault, "Create Vault");
        SetNavLabel(NavSettings, "Settings");
    }

    private void SetNavLabel(RadioButton item, string label)
    {
        item.Content = _navCollapsed ? string.Empty : label;
        item.ToolTip = _navCollapsed ? label : null;
    }

    // ------------------------------------------------------------------ session chrome

    private void UpdateSessionChrome()
    {
        int count = App.Current.Sessions.Sessions.Count;

        if (count == 0)
        {
            SessionStatusText.Text = "All vaults locked";
            UnlockedBadge.Data = (Geometry)FindResource("GeoLock");
            UnlockedBadge.SetResourceReference(IconPresenter.ForegroundProperty, "TextFillColorTertiaryBrush");
            LockAllButton.IsEnabled = false;

            RailLockGlyph.Data = (Geometry)FindResource("GeoLock");
            RailLockGlyph.SetResourceReference(IconPresenter.ForegroundProperty, "TextFillColorTertiaryBrush");
            RailLockButton.IsEnabled = false;
            RailLockButton.ToolTip = "All vaults locked";
        }
        else
        {
            SessionStatusText.Text = count == 1 ? "1 vault unlocked" : $"{count} vaults unlocked";
            UnlockedBadge.Data = (Geometry)FindResource("GeoUnlock");
            UnlockedBadge.SetResourceReference(IconPresenter.ForegroundProperty, "SystemFillColorSuccessBrush");
            LockAllButton.IsEnabled = true;

            RailLockGlyph.Data = (Geometry)FindResource("GeoUnlock");
            RailLockGlyph.SetResourceReference(IconPresenter.ForegroundProperty, "SystemFillColorSuccessBrush");
            RailLockButton.IsEnabled = true;
            RailLockButton.ToolTip = count == 1 ? "Lock the unlocked vault" : $"Lock all {count} vaults";
        }

        _vaultsPage?.Refresh();
    }

    private async void LockAll_Click(object sender, RoutedEventArgs e)
    {
        var sessions = App.Current.Sessions.Sessions;
        if (sessions.Count == 0) return;

        string names = string.Join("\n• ", sessions.Select(s => s.DisplayName));

        var answer = await DialogLayer.ShowAsync(
            sessions.Count == 1 ? "Lock this vault?" : "Lock all vaults?",
            $"KnightLock will seal any changes back into the container, then securely wipe the decrypted copies.\n\n• {names}",
            "Lock now",
            "Lock without saving",
            "Cancel",
            DialogSeverity.Warning);

        if (answer == DialogResultKind.Close) return;

        DialogLayer.ShowBusy("Locking vaults…", "Sealing changes and wiping decrypted files…");
        try
        {
            await App.Current.Sessions.LockAllAsync(sealChanges: answer == DialogResultKind.Primary);
            DialogLayer.HideBusy();
            UpdateSessionChrome();
        }
        catch (Exception ex)
        {
            DialogLayer.HideBusy();
            var vex = VaultException.From(ex);
            await DialogLayer.ShowErrorAsync(vex.Message, vex.Title);
        }
    }

    // ------------------------------------------------------------------ shutdown

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        var settings = App.Current.Settings;
        settings.WindowWidth = Math.Max(940, ActualWidth);
        settings.WindowHeight = Math.Max(640, ActualHeight);
        settings.Save();

        if (_shutdownReady) return;

        var sessions = App.Current.Sessions.Sessions;
        if (sessions.Count == 0)
        {
            _shutdownReady = true;
            return;
        }

        e.Cancel = true;

        var answer = await DialogLayer.ShowAsync(
            "Lock vaults before closing?",
            "Decrypted files are still on this PC. KnightLock can seal every change back into " +
            "your containers and wipe the plaintext before it closes.",
            "Lock and close",
            "Discard changes and close",
            "Stay open",
            DialogSeverity.Warning);

        if (answer == DialogResultKind.Close)
        {
            UpdateSessionChrome();
            return;
        }

        DialogLayer.ShowBusy("Locking vaults…", "Sealing changes and wiping decrypted files…");
        try
        {
            await App.Current.Sessions.LockAllAsync(sealChanges: answer == DialogResultKind.Primary);
        }
        catch (Exception ex)
        {
            Log.Error("Sealing during shutdown failed", ex);
        }

        DialogLayer.HideBusy();
        _shutdownReady = true;
        Close();
    }
}
