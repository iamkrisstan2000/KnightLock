using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using KnightLock.Controls;
using KnightLock.Core;
using KnightLock.Services;

namespace KnightLock;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    public static new App Current => (App)Application.Current;

    public AppSettings Settings { get; private set; } = new();
    public VaultRegistry Registry { get; private set; } = null!;
    public SessionManager Sessions { get; private set; } = null!;

    /// <summary>The shell's ContentDialog host, available to every page.</summary>
    public static ContentDialogHost? Dialogs { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // A container handed over by the shell: double-click, "Open with", or a shortcut drop.
        string? requestedVault = FirstVaultArgument(e.Args);

        // One vault manager per user session, so two windows never fight over a container.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\KnightLock.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            // Already running: hand the path over instead of refusing to start.
            if (requestedVault is not null && ShellBridge.TrySend(requestedVault))
            {
                Shutdown();
                return;
            }

            MessageBox.Show("KnightLock is already running.", "KnightLock",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled AppDomain exception", args.ExceptionObject as Exception);

        AppPaths.EnsureCreated();

        Settings = AppSettings.Load();
        ThemeService.Apply(ThemeService.Resolve(Settings.NormalizedTheme), Settings.UseSystemAccent);

        Registry = new VaultRegistry(AppPaths.VaultLibraryFile);
        int missing = Registry.PruneMissing();
        if (missing > 0) Log.Info($"Removed {missing} vault library entries whose files no longer exist.");

        Sessions = new SessionManager(Registry, Settings);

        // Anything a crash left decrypted on disk is destroyed before the UI appears.
        Sessions.PurgeOrphanedSessions();
        Sessions.StartAutoLock();

        Log.Info($"KnightLock started (build {WindowsInteropVersion()}, windows {Environment.OSVersion.Version}).");

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // Double-clicking a .vault while KnightLock runs routes here through the named pipe.
        ShellBridge.VaultRequested += path => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;

            window.Activate();
            window.OpenVaultFromShell(path);
        }));
        ShellBridge.StartServer();

        if (requestedVault is not null)
            window.OpenVaultFromShell(requestedVault);
    }

    /// <summary>First command-line argument that names an existing .vault container.</summary>
    private static string? FirstVaultArgument(string[] args)
    {
        foreach (string arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg)) continue;

            string path = arg.Trim().Trim('"');
            if (!path.EndsWith(VaultService.VaultExtension, StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(path)) return path;
        }

        return null;
    }

    private static string WindowsInteropVersion() =>
        typeof(App).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);

        try
        {
            if (Dialogs is not null)
            {
                Dialogs.HideBusy();
                _ = Dialogs.ShowAsync("Unexpected error",
                    $"{e.Exception.Message}\n\nKnightLock kept your vault data safe. " +
                    "If this keeps happening, check the log at %LOCALAPPDATA%\\KnightLock\\logs.",
                    "OK", closeText: null, severity: DialogSeverity.Error);
            }
            else
            {
                MessageBox.Show(
                    $"{e.Exception.Message}\n\nIf this happened at startup, the log at " +
                    "%LOCALAPPDATA%\\KnightLock\\logs has the details.",
                    "KnightLock — Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);

                // Nothing usable to keep alive if the shell never came up.
                if (MainWindow is null) Shutdown(-1);
            }
        }
        catch { }

        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (Sessions is not null)
            {
                // Seal edits back into their containers (bounded wait), then destroy plaintext.
                var sealTask = Sessions.LockAllAsync(Settings.SealOnExit);
                Task.WaitAny(new Task[] { sealTask }, TimeSpan.FromSeconds(45));
                Sessions.WipeAllImmediately();
            }
            Settings?.Save();
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown cleanup failed", ex);
        }
        finally
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }

        base.OnExit(e);
    }

    /// <summary>Convenience wrapper used across the views.</summary>
    public static ContentDialogHost Shell => Dialogs ??
        throw new InvalidOperationException("Dialog host is not initialised yet.");

    public static void OpenInExplorer(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path) || System.IO.File.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = true
                });
        }
        catch (Exception ex)
        {
            Log.Error("Could not open Explorer", ex);
        }
    }

    public static void CopyToClipboard(string text)
    {
        try { Clipboard.SetText(text); } catch { }
    }
}
