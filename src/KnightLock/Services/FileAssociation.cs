using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace KnightLock.Services;

/// <summary>
/// Registers KnightLock as a handler for <c>.vault</c> containers.
///
/// Everything is written under HKCU\Software\Classes, so no elevation is needed and nothing is
/// changed for other users. Windows 11 keeps the user's explicit per-file-type choice in
/// UserChoice, which no application may override — when one exists, Explorer keeps honouring it
/// and the user has to confirm KnightLock once in Settings &gt; Default apps. That is a deliberate
/// OS guard rail, not something the app can or should bypass.
/// </summary>
public static class FileAssociation
{
    private const string ExtensionKey = @"Software\Classes\.vault";
    private const string ProgId = "KnightLock.Vault";
    private const string ProgIdKey = @"Software\Classes\KnightLock.Vault";
    private const string UserChoiceKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.vault\UserChoice";

    /// <summary>Path to the running executable (the single-file host when published).</summary>
    public static string ExecutablePath
    {
        get
        {
            string? exe = Environment.ProcessPath;

            // Under `dotnet KnightLock.dll` the process image is dotnet.exe, which must never be
            // registered as the .vault handler -- fall back to the app's own launcher instead.
            if (!string.IsNullOrEmpty(exe)
                && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetFileName(exe), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                return exe;
            }

            return Path.Combine(AppContext.BaseDirectory, "KnightLock.exe");
        }
    }

    private static string OpenCommand => $"\"{ExecutablePath}\" \"%1\"";

    /// <summary>True when .vault is mapped to this executable.</summary>
    public static bool IsRegistered()
    {
        try
        {
            using var ext = Registry.CurrentUser.OpenSubKey(ExtensionKey);
            if (!string.Equals(ext?.GetValue(null) as string, ProgId, StringComparison.OrdinalIgnoreCase))
                return false;

            using var command = Registry.CurrentUser.OpenSubKey(ProgIdKey + @"\shell\open\command");
            return string.Equals(command?.GetValue(null) as string, OpenCommand, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when Windows holds an explicit user choice that outranks this registration.</summary>
    public static bool HasUserChoiceOverride
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(UserChoiceKey);
                return key?.GetValue("ProgId") is string progId &&
                       !progId.Equals(ProgId, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Writes the association and tells Explorer to refresh icons.</summary>
    public static bool Register(out string error)
    {
        error = string.Empty;
        try
        {
            using (var ext = Registry.CurrentUser.CreateSubKey(ExtensionKey, writable: true))
                ext?.SetValue(null, ProgId);

            using (var progId = Registry.CurrentUser.CreateSubKey(ProgIdKey, writable: true))
            {
                progId?.SetValue(null, "KnightLock encrypted vault");

                using var icon = progId?.CreateSubKey("DefaultIcon", writable: true);
                icon?.SetValue(null, $"\"{ExecutablePath}\",0");

                using var command = progId?.CreateSubKey(@"shell\open\command", writable: true);
                command?.SetValue(null, OpenCommand);

                using var menu = progId?.CreateSubKey(@"shell\open", writable: true);
                menu?.SetValue("FriendlyAppName", "KnightLock");
            }

            NotifyShell();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Removes only the keys this app owns.</summary>
    public static bool Unregister(out string error)
    {
        error = string.Empty;
        try
        {
            using (var ext = Registry.CurrentUser.OpenSubKey(ExtensionKey, writable: true))
            {
                if (string.Equals(ext?.GetValue(null) as string, ProgId, StringComparison.OrdinalIgnoreCase))
                    ext?.SetValue(null, string.Empty);
            }

            Registry.CurrentUser.DeleteSubKeyTree(ProgIdKey, throwOnMissingSubKey: false);
            NotifyShell();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Opens Windows' Default apps page so the user can confirm or change the handler.</summary>
    public static void OpenDefaultAppsSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
        }
        catch
        {
            // Settings app unavailable (rare, e.g. stripped-down SKU) — nothing else to do.
        }
    }

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private static void NotifyShell() => SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
}
