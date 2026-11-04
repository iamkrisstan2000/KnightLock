using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using KnightLock.Controls;
using KnightLock.Core;
using KnightLock.Interop;
using KnightLock.Services;

namespace KnightLock.Views;

public partial class SettingsPage : UserControl
{
    private bool _initializing;
    private bool _lastMaterialApplied;
    private BackdropKind _lastBackdrop = BackdropKind.Mica;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => OnNavigatedTo();
    }

    public void OnNavigatedTo()
    {
        _initializing = true;
        try
        {
            var settings = App.Current.Settings;

            ThemeSystem.IsChecked = settings.NormalizedTheme == ThemePreference.System;
            ThemeLight.IsChecked = settings.NormalizedTheme == ThemePreference.Light;
            ThemeDark.IsChecked = settings.NormalizedTheme == ThemePreference.Dark;

            BackdropMica.IsChecked = settings.NormalizedBackdrop == BackdropPreference.Mica;
            BackdropAcrylic.IsChecked = settings.NormalizedBackdrop == BackdropPreference.Acrylic;
            BackdropSolid.IsChecked = settings.NormalizedBackdrop == BackdropPreference.Solid;

            AccentToggle.IsChecked = settings.UseSystemAccent;

            IterFast.IsChecked = settings.Iterations <= 400_000;
            IterRecommended.IsChecked = settings.Iterations > 400_000 && settings.Iterations <= 900_000;
            IterParanoid.IsChecked = settings.Iterations > 900_000;

            Wipe1.IsChecked = settings.WipePasses <= 1;
            Wipe2.IsChecked = settings.WipePasses == 2;
            Wipe3.IsChecked = settings.WipePasses >= 3;

            AutoOff.IsChecked = settings.AutoLockMinutes == 0;
            Auto5.IsChecked = settings.AutoLockMinutes > 0 && settings.AutoLockMinutes <= 5;
            Auto10.IsChecked = settings.AutoLockMinutes > 5 && settings.AutoLockMinutes <= 10;
            Auto30.IsChecked = settings.AutoLockMinutes > 10;

            OpenExplorerToggle.IsChecked = settings.OpenExplorerAfterUnlock;
            ConfirmToggle.IsChecked = settings.ConfirmBeforeLocking;
            SealOnExitToggle.IsChecked = settings.SealOnExit;

            AssociateToggle.IsChecked = FileAssociation.IsRegistered();
            RefreshAssociationStatus();

            RefreshFacts();
            RefreshBackdropStatus(_lastMaterialApplied, _lastBackdrop);
        }
        finally
        {
            _initializing = false;
        }
    }

    private void RefreshFacts()
    {
        var registry = App.Current.Registry;
        int records = registry.Records.Count;
        int existing = registry.Records.Count(r => r.Exists);
        int sessions = App.Current.Sessions.Sessions.Count;

        LibraryCountText.Text = $"{records} vault{(records == 1 ? "" : "s")} in your library ({existing} reachable).";
        SessionsText.Text = sessions == 0
            ? "No vaults are unlocked right now — nothing is decrypted on this PC."
            : $"{sessions} vault{(sessions == 1 ? "" : "s")} unlocked. Decrypted copies live in the session folder below.";

        PathsText.Text =
            $"library    {AppPaths.VaultLibraryFile}\n" +
            $"settings   {AppPaths.SettingsFile}\n" +
            $"sessions   {AppPaths.SessionsRoot}\n" +
            $"log        {AppPaths.LogFile}";

        int iterations = App.Current.Settings.Iterations;
        VersionInfoText.Text = $"Version {typeof(SettingsPage).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"} · native Windows (WPF, no Electron)";
        EngineInfoText.Text =
            $"container  KnightLock Vault v{VaultFormat.CurrentVersion} (.vault)\n" +
            $"cipher     AES-256-GCM, {VaultFormat.ChunkSize / (1024 * 1024)} MiB chunks, unique nonce per chunk\n" +
            $"kdf        PBKDF2-HMAC-SHA256 · {iterations:N0} rounds · 128-bit salt\n" +
            $"integrity  GCM tag + header fingerprint bound into every chunk\n" +
            $"os         Windows {Environment.OSVersion.Version} (build {WindowsInterop.WindowsBuild})";
    }

    /// <summary>Called by MainWindow after it applies (or fails to apply) the window material.</summary>
    public void RefreshBackdropStatus(bool materialApplied, BackdropKind kind)
    {
        _lastMaterialApplied = materialApplied;
        _lastBackdrop = kind;

        if (BackdropStatus is null) return;

        int build = WindowsInterop.WindowsBuild;

        BackdropStatus.Text = materialApplied
            ? kind switch
            {
                BackdropKind.Mica => $"Mica is active (Windows build {build}).",
                BackdropKind.Acrylic => $"Acrylic is active (Windows build {build}).",
                _ => $"System material active (Windows build {build})."
            }
            : kind == BackdropKind.None
                ? "Solid surface selected — no translucency is used."
                : $"This PC (Windows build {build}) cannot composite that material, so KnightLock is painting a solid Fluent surface instead.";
    }

    // ------------------------------------------------------------------ handlers

    private void Save() => App.Current.Settings.Save();

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not RadioButton { Tag: string tag }) return;

        var preference = tag switch
        {
            "Light" => ThemePreference.Light,
            "Dark" => ThemePreference.Dark,
            _ => ThemePreference.System
        };

        App.Current.Settings.Theme = preference;
        Save();

        ThemeService.Apply(ThemeService.Resolve(preference), App.Current.Settings.UseSystemAccent);
        (Window.GetWindow(this) as MainWindow)?.ReapplyAppearance();
    }

    private void Backdrop_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not RadioButton { Tag: string tag }) return;

        App.Current.Settings.Backdrop = tag switch
        {
            "Acrylic" => BackdropPreference.Acrylic,
            "Solid" => BackdropPreference.Solid,
            _ => BackdropPreference.Mica
        };

        Save();
        (Window.GetWindow(this) as MainWindow)?.ReapplyAppearance();
    }

    private void AccentToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        App.Current.Settings.UseSystemAccent = AccentToggle.IsChecked == true;
        Save();

        ThemeService.Apply(ThemeService.Resolve(App.Current.Settings.NormalizedTheme),
            App.Current.Settings.UseSystemAccent);
        (Window.GetWindow(this) as MainWindow)?.ReapplyAppearance();
    }

    private void Iterations_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not RadioButton { Tag: string tag }) return;
        if (!int.TryParse(tag, out int iterations)) return;

        App.Current.Settings.Iterations = Math.Clamp(iterations,
            VaultFormat.MinimumIterations, VaultFormat.MaximumIterations);
        Save();
        RefreshFacts();
    }

    private void Wipe_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not RadioButton { Tag: string tag }) return;
        if (!int.TryParse(tag, out int passes)) return;

        App.Current.Settings.WipePasses = Math.Clamp(passes, 1, 3);
        Save();
        RefreshFacts();
    }

    private void AutoLock_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not RadioButton { Tag: string tag }) return;
        if (!int.TryParse(tag, out int minutes)) return;

        App.Current.Settings.AutoLockMinutes = Math.Clamp(minutes, 0, 240);
        Save();
    }

    // ------------------------------------------------------------------ shell integration

    private void AssociateToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        if (AssociateToggle.IsChecked == true)
        {
            if (!FileAssociation.Register(out string error))
            {
                AssociateToggle.IsChecked = false;
                _ = App.Shell.ShowAsync("Could not register .vault files",
                    $"{error}\n\nKnightLock can still open containers through \u201cBrowse for .vault\u2026\u201d.",
                    "OK", closeText: null, severity: DialogSeverity.Error);
                return;
            }
        }
        else
        {
            FileAssociation.Unregister(out _);
        }

        RefreshAssociationStatus();
    }

    private void RefreshAssociationStatus()
    {
        if (AssociateStatus is null) return;

        if (!FileAssociation.IsRegistered())
            AssociateStatus.Text = "KnightLock is not the default handler for .vault files.";
        else if (FileAssociation.HasUserChoiceOverride)
            AssociateStatus.Text = "Registered, but Windows remembers a different app for .vault. "
                                 + "Open Default apps and pick KnightLock once to override it.";
        else
            AssociateStatus.Text = "Registered \u2014 double-clicking a .vault file opens it here.";
    }

    private void OpenDefaultApps_Click(object sender, RoutedEventArgs e) => FileAssociation.OpenDefaultAppsSettings();

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        var settings = App.Current.Settings;
        settings.OpenExplorerAfterUnlock = OpenExplorerToggle.IsChecked == true;
        settings.ConfirmBeforeLocking = ConfirmToggle.IsChecked == true;
        settings.SealOnExit = SealOnExitToggle.IsChecked == true;
        Save();
    }

    private void OpenAppData_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.RoamingRoot);
        App.OpenInExplorer(AppPaths.RoamingRoot);
    }

    private void OpenSessions_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.SessionsRoot);
        App.OpenInExplorer(AppPaths.SessionsRoot);
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(AppPaths.LogFile)) App.OpenInExplorer(AppPaths.LogFile);
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.LogFile)!);
            App.OpenInExplorer(Path.GetDirectoryName(AppPaths.LogFile)!);
        }
    }

    private async void Prune_Click(object sender, RoutedEventArgs e)
    {
        int removed = App.Current.Registry.PruneMissing();
        RefreshFacts();

        await App.Shell.ShowAsync(
            removed == 0 ? "Library is up to date" : "Missing vaults removed",
            removed == 0
                ? "Every vault in your library is still reachable."
                : $"{removed} entr{(removed == 1 ? "y" : "ies")} pointed at files that no longer exist and were removed.",
            "OK", closeText: null, severity: DialogSeverity.Info);
    }

    private async void WipeSessions_Click(object sender, RoutedEventArgs e)
    {
        int sessions = App.Current.Sessions.Sessions.Count;

        var answer = await App.Shell.ShowAsync(
            "Wipe decrypted files now?",
            sessions == 0
                ? "No vaults are unlocked. KnightLock will still scrub the session folder to remove any leftover plaintext."
                : $"{sessions} unlocked vault{(sessions == 1 ? "" : "s")} will be locked WITHOUT saving changes. " +
                  "Every decrypted file is overwritten and deleted.",
            "Wipe now",
            closeText: "Cancel",
            severity: DialogSeverity.Warning);

        if (answer != DialogResultKind.Primary) return;

        App.Shell.ShowBusy("Wiping decrypted files…", "Overwriting plaintext with random data…");
        try
        {
            await App.Current.Sessions.LockAllAsync(sealChanges: false);
            App.Current.Sessions.PurgeOrphanedSessions();
        }
        catch (Exception ex)
        {
            Log.Error("Wipe all failed", ex);
        }
        finally
        {
            App.Shell.HideBusy();
            RefreshFacts();
        }

        await App.Shell.ShowAsync("Decrypted files wiped",
            "The session folder is empty and the plaintext is gone.",
            "OK", closeText: null, severity: DialogSeverity.Success);
    }

    private async void Forget_Click(object sender, RoutedEventArgs e)
    {
        var answer = await App.Shell.ShowAsync(
            "Forget all vaults?",
            "KnightLock will clear its library list. The .vault files themselves are NOT deleted and can be added back at any time.",
            "Forget all",
            closeText: "Cancel",
            severity: DialogSeverity.Warning);

        if (answer != DialogResultKind.Primary) return;

        await App.Current.Sessions.LockAllAsync(sealChanges: true);
        App.Current.Registry.ClearAll();
        RefreshFacts();

        await App.Shell.ShowAsync("Library cleared",
            "KnightLock no longer lists any vaults. Your .vault files are untouched.",
            "OK", closeText: null, severity: DialogSeverity.Success);
    }
}
