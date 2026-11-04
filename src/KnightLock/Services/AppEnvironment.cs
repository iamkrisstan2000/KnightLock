using System;
using System.IO;
using System.Text;

namespace KnightLock.Services;

/// <summary>Well-known KnightLock paths (%APPDATA%, %LOCALAPPDATA%).</summary>
public static class AppPaths
{
    public static string RoamingRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KnightLock");

    public static string LocalRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KnightLock");

    public static string SettingsFile => Path.Combine(RoamingRoot, "settings.json");
    public static string VaultLibraryFile => Path.Combine(RoamingRoot, "vaults.json");
    public static string SessionsRoot => Path.Combine(LocalRoot, "Sessions");
    public static string LogFile => Path.Combine(LocalRoot, "logs", "knightlock.log");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(RoamingRoot);
        Directory.CreateDirectory(LocalRoot);
        Directory.CreateDirectory(SessionsRoot);
    }
}

/// <summary>
/// Tiny append-only diagnostic log. Contains no secrets — never passwords, keys or
/// file contents. Capped at 256 KB so it can never grow without bound.
/// </summary>
public static class Log
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} — {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                string? dir = Path.GetDirectoryName(AppPaths.LogFile);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var info = new FileInfo(AppPaths.LogFile);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(AppPaths.LogFile, AppPaths.LogFile + ".old", overwrite: true);

                File.AppendAllText(AppPaths.LogFile,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
