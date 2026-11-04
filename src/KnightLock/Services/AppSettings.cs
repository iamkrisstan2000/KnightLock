using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using KnightLock.Core;

namespace KnightLock.Services;

public enum ThemePreference
{
    System = 0,
    Light = 1,
    Dark = 2
}

public enum BackdropPreference
{
    Mica = 0,
    Acrylic = 1,
    Solid = 2
}

/// <summary>User preferences, persisted as JSON under %APPDATA%\KnightLock\settings.json.</summary>
public sealed class AppSettings
{
    [JsonPropertyName("theme")] public ThemePreference Theme { get; set; } = ThemePreference.System;
    [JsonPropertyName("backdrop")] public BackdropPreference Backdrop { get; set; } = BackdropPreference.Mica;
    [JsonPropertyName("useSystemAccent")] public bool UseSystemAccent { get; set; } = true;
    [JsonPropertyName("iterations")] public int Iterations { get; set; } = VaultFormat.DefaultIterations;
    [JsonPropertyName("wipePasses")] public int WipePasses { get; set; } = SecureDelete.DefaultPasses;
    [JsonPropertyName("autoLockMinutes")] public int AutoLockMinutes { get; set; } = 10;
    [JsonPropertyName("openExplorerAfterUnlock")] public bool OpenExplorerAfterUnlock { get; set; } = true;
    [JsonPropertyName("sealOnExit")] public bool SealOnExit { get; set; } = true;
    [JsonPropertyName("confirmBeforeLocking")] public bool ConfirmBeforeLocking { get; set; } = true;
    [JsonPropertyName("lastSourceFolder")] public string LastSourceFolder { get; set; } = "";
    [JsonPropertyName("lastVaultFolder")] public string LastVaultFolder { get; set; } = "";
    [JsonPropertyName("windowWidth")] public double WindowWidth { get; set; } = 1180;
    [JsonPropertyName("windowHeight")] public double WindowHeight { get; set; } = 780;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                string json = File.ReadAllText(AppPaths.SettingsFile);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded is not null) return loaded.Normalized();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings could not be read, using defaults ({ex.GetType().Name}).");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.RoamingRoot);
            string tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Normalized(), Options));
            File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings could not be saved ({ex.GetType().Name}).");
        }
    }

    private AppSettings Normalized()
    {
        Iterations = Math.Clamp(Iterations, VaultFormat.MinimumIterations, VaultFormat.MaximumIterations);
        WipePasses = Math.Clamp(WipePasses, 1, 3);
        AutoLockMinutes = Math.Clamp(AutoLockMinutes, 0, 240);
        if (WindowWidth < 900) WindowWidth = 1180;
        if (WindowHeight < 640) WindowHeight = 780;
        return this;
    }

    public ThemePreference NormalizedTheme => Enum.IsDefined(Theme) ? Theme : ThemePreference.System;
    public BackdropPreference NormalizedBackdrop => Enum.IsDefined(Backdrop) ? Backdrop : BackdropPreference.Mica;
}
