using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KnightLock.Core;

/// <summary>A vault the user has added to their library (no secrets are stored here).</summary>
public sealed class VaultRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("D");
    [JsonPropertyName("name")] public string Name { get; set; } = "Vault";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("created")] public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("lastOpened")] public DateTime? LastOpenedUtc { get; set; }
    [JsonPropertyName("bytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("files")] public int FileCount { get; set; }
    [JsonPropertyName("source")] public string SourceFolder { get; set; } = "";

    [JsonIgnore] public bool Exists => !string.IsNullOrEmpty(Path) && File.Exists(Path);
}

/// <summary>
/// Local, non-secret library of known vaults, persisted under %APPDATA%\KnightLock.
/// Writes are atomic (temp file + replace) so a crash can't corrupt the library.
/// </summary>
public sealed class VaultRegistry
{
    private readonly string _filePath;
    private List<VaultRecord> _records = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    public VaultRegistry(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    public static string DefaultPath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KnightLock", "vaults.json");

    public IReadOnlyList<VaultRecord> Records => _records;

    public void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) { _records = new(); return; }
            string json = File.ReadAllText(_filePath);
            _records = JsonSerializer.Deserialize<List<VaultRecord>>(json, Options) ?? new();
        }
        catch
        {
            _records = new(); // never let a corrupt library stop the app from starting
        }
    }

    public void Save()
    {
        string? dir = System.IO.Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_records, Options));
        if (File.Exists(_filePath)) File.Delete(_filePath);
        File.Move(tmp, _filePath);
    }

    public VaultRecord? Find(string path) =>
        _records.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));

    public VaultRecord AddOrUpdate(VaultRecord record)
    {
        var existing = Find(record.Path);
        if (existing is not null)
        {
            existing.Name = record.Name;
            existing.SizeBytes = record.SizeBytes;
            existing.FileCount = record.FileCount;
            existing.SourceFolder = record.SourceFolder;
            existing.CreatedUtc = record.CreatedUtc;
            existing.LastOpenedUtc = record.LastOpenedUtc ?? existing.LastOpenedUtc;
            Save();
            return existing;
        }

        _records.Insert(0, record);
        Save();
        return record;
    }

    public void Touch(string path)
    {
        var rec = Find(path);
        if (rec is null) return;
        rec.LastOpenedUtc = DateTime.UtcNow;
        try { rec.SizeBytes = new FileInfo(path).Length; } catch { }
        Save();
    }

    public void Remove(string path)
    {
        _records.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    public void ClearAll()
    {
        _records.Clear();
        Save();
    }

    /// <summary>Drop entries whose container file has been moved or deleted.</summary>
    public int PruneMissing()
    {
        int removed = _records.RemoveAll(r => !r.Exists);
        if (removed > 0) Save();
        return removed;
    }
}
