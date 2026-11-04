using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using KnightLock.Controls;
using KnightLock.Core;
using KnightLock.Services;

namespace KnightLock.Views;

public partial class MyVaultsPage : UserControl
{
    private readonly ObservableCollection<VaultCardItem> _items = new();
    private VaultCardItem? _selected;
    private EventHandler? _busyCancelHandler;

    public MyVaultsPage()
    {
        InitializeComponent();

        VaultList.ItemsSource = _items;

        Loaded += (_, _) => Refresh();

        if (App.Current?.Sessions is not null)
            App.Current.Sessions.AutoLocked += OnAutoLocked;
    }

    // ------------------------------------------------------------------ state

    public void Refresh()
    {
        if (App.Current?.Registry is null) return;

        var registry = App.Current.Registry;
        registry.PruneMissing();

        string? previouslySelected = _selected?.FullPath;

        _items.Clear();
        foreach (var record in registry.Records
                     .OrderByDescending(r => r.LastOpenedUtc ?? r.CreatedUtc))
        {
            _items.Add(new VaultCardItem(record)
            {
                IsUnlocked = App.Current.Sessions.IsUnlocked(record.Path)
            });
        }

        _selected = _items.FirstOrDefault(i =>
                        string.Equals(i.FullPath, previouslySelected, StringComparison.OrdinalIgnoreCase))
                    ?? _items.FirstOrDefault();

        ApplySelection();
        UpdateEmptyState();
        UpdateSubtitle();
    }

    /// <summary>
    /// Selects (and, if new, registers) a container handed over by the shell. Any problem is
    /// reported with a dialog rather than silently ignored, because the user just double-clicked
    /// the file and expects *something* to happen.
    /// </summary>
    public void OpenFromShell(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            if (!System.IO.File.Exists(path))
            {
                _ = App.Shell.ShowAsync("Vault not found",
                    $"KnightLock could not open this container because the file is no longer there.\n\n{path}",
                    "OK", closeText: null, severity: DialogSeverity.Error);
                return;
            }

            var header = VaultService.Inspect(path);

            App.Current.Registry.AddOrUpdate(new VaultRecord
            {
                Id = header.VaultId,
                Name = header.Name,
                Path = path,
                CreatedUtc = header.CreatedUtc,
                SizeBytes = new System.IO.FileInfo(path).Length,
                FileCount = header.FileCount,
                SourceFolder = header.SourceFolder
            });

            Refresh();

            _selected = _items.FirstOrDefault(i =>
                string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));
            ApplySelection();

            Dispatcher.BeginInvoke(new Action(() => DetailPassword?.FocusInput()));
        }
        catch (Exception ex)
        {
            _ = ShowVaultErrorAsync(ex);
        }
    }

    private void UpdateEmptyState()
    {
        bool empty = _items.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ListScroller.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateSubtitle()
    {
        int unlocked = _items.Count(i => i.IsUnlocked);

        SubtitleText.Text = _items.Count switch
        {
            0 => "Unlock a vault to work with its files. Everything is sealed again when you lock it.",
            _ when unlocked == 0 => $"{_items.Count} vault{(_items.Count == 1 ? "" : "s")} in your library · all locked",
            _ => $"{unlocked} of {_items.Count} unlocked — decrypted files live in a private temp folder until you lock"
        };
    }

    private void ApplySelection()
    {
        foreach (var item in _items) item.IsSelected = ReferenceEquals(item, _selected);
        UpdateDetail();
    }

    private void UpdateDetail()
    {
        if (_selected is null)
        {
            DetailEmpty.Visibility = Visibility.Visible;
            DetailPanel.Visibility = Visibility.Collapsed;
            return;
        }

        DetailEmpty.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;

        DetailName.Text = _selected.Name;
        DetailPath.Text = _selected.FullPath;
        DetailSize.Text = _selected.SizeText;
        DetailFiles.Text = _selected.FilesText;
        DetailCreated.Text = _selected.Record.CreatedUtc.ToLocalTime().ToString("d MMM yyyy");
        DetailOpened.Text = _selected.Record.LastOpenedUtc is DateTime opened
            ? opened.ToLocalTime().ToString("d MMM yyyy, HH:mm")
            : "never";

        DetailGlyph.Data = (System.Windows.Media.Geometry)FindResource(
            _selected.IsUnlocked ? "GeoUnlock" : "GeoLock");

        var session = App.Current.Sessions.Find(_selected.FullPath);

        if (session is not null)
        {
            LockedPanel.Visibility = Visibility.Collapsed;
            UnlockedPanel.Visibility = Visibility.Visible;

            UnlockedText.Text = $"Unlocked — {session.Header.FileCount:N0} files decrypted to a private folder.";

            var changes = session.DetectChanges();
            ChangesText.Text = changes.HasChanges
                ? $"{changes.Describe()} since unlocking. Locking will offer to seal them back into the container."
                : "No changes yet. Anything you edit in the folder is sealed back when you lock.";
        }
        else
        {
            LockedPanel.Visibility = Visibility.Visible;
            UnlockedPanel.Visibility = Visibility.Collapsed;
            DetailPassword.Clear();
            PasswordHint.Visibility = Visibility.Collapsed;
            UnlockButton.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------------ list interaction

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: VaultCardItem item })
        {
            _selected = item;
            ApplySelection();
        }
    }

    // ------------------------------------------------------------------ unlock / lock

    private async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;

        string password = DetailPassword.Password;
        if (string.IsNullOrEmpty(password))
        {
            PasswordHint.Text = "Enter the master password for this vault.";
            PasswordHint.Visibility = Visibility.Visible;
            DetailPassword.FocusInput();
            return;
        }

        PasswordHint.Visibility = Visibility.Collapsed;

        var cts = BeginBusy("Unlocking vault…", "Deriving key…", allowCancel: true);
        var progress = new Progress<VaultProgress>(p => App.Shell.UpdateBusy(p.Message, p.Percent));

        try
        {
            var result = await App.Current.Sessions.UnlockAsync(_selected.FullPath, password, progress, cts.Token);
            EndBusy();

            DetailPassword.Clear();
            Refresh();

            if (App.Current.Settings.OpenExplorerAfterUnlock)
                App.OpenInExplorer(result.Session.WorkingDirectory);

            var answer = await App.Shell.ShowAsync(
                "Vault unlocked",
                $"\"{result.Session.DisplayName}\" is open. {result.Session.Header.FileCount:N0} files were decrypted " +
                $"in {result.Duration.TotalSeconds:0.0}s.\n\nClose Windows Explorer and use Lock vault when you are done — " +
                "the decrypted copy is wiped from disk.",
                "Open folder", null, "Done", DialogSeverity.Success);

            if (answer == DialogResultKind.Primary)
                App.OpenInExplorer(result.Session.WorkingDirectory);
        }
        catch (OperationCanceledException)
        {
            EndBusy();
            DetailPassword.Clear();
        }
        catch (Exception ex)
        {
            EndBusy();
            DetailPassword.Clear();
            await ShowVaultErrorAsync(ex);
        }
    }

    private void DetailPassword_PasswordChanged(object sender, EventArgs e)
        => PasswordHint.Visibility = Visibility.Collapsed;

    private void DetailPassword_Submitted(object sender, EventArgs e) => Unlock_Click(sender, new RoutedEventArgs());

    private async void Lock_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;

        var session = App.Current.Sessions.Find(_selected.FullPath);
        if (session is null)
        {
            Refresh();
            return;
        }

        bool sealChanges = true;
        var changes = session.DetectChanges();

        if (changes.HasChanges && App.Current.Settings.ConfirmBeforeLocking)
        {
            var answer = await App.Shell.ShowAsync(
                "Save changes to the vault?",
                $"{changes.Describe()} in the unlocked folder.\n\n" +
                "Save and lock re-encrypts the folder into the container, then wipes the plaintext.",
                "Save and lock",
                "Discard changes",
                "Cancel",
                DialogSeverity.Warning);

            if (answer == DialogResultKind.Close) return;
            sealChanges = answer == DialogResultKind.Primary;
        }

        var cts = BeginBusy(sealChanges ? "Sealing and locking…" : "Locking vault…",
            "Re-encrypting changes…", allowCancel: false);
        var progress = new Progress<VaultProgress>(p => App.Shell.UpdateBusy(p.Message, p.Percent));

        try
        {
            var sealResult = await App.Current.Sessions.LockAsync(_selected.FullPath, sealChanges, progress, cts.Token);
            EndBusy();
            Refresh();

            string message = sealResult is not null
                ? $"Changes saved: {sealResult.FileCount:N0} files re-encrypted into the container " +
                  $"({ByteSizeText.Format(sealResult.SourceBytes)} → {ByteSizeText.Format(sealResult.VaultBytes)}).\n\n" +
                  "The decrypted copy has been overwritten and removed from disk."
                : "The decrypted copy has been overwritten and removed from disk. The container is unchanged.";

            await App.Shell.ShowAsync($"\"{session.DisplayName}\" locked", message,
                "Done", closeText: null, severity: DialogSeverity.Success);
        }
        catch (Exception ex)
        {
            EndBusy();
            await ShowVaultErrorAsync(ex);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;

        var session = App.Current.Sessions.Find(_selected.FullPath);
        if (session is null)
        {
            _ = App.Shell.ShowAsync("This vault is locked",
                "Unlock the vault first, then open its folder.",
                "OK", closeText: null, severity: DialogSeverity.Warning);
            Refresh();
            return;
        }

        App.OpenInExplorer(session.WorkingDirectory);
    }

    // ------------------------------------------------------------------ library commands

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a KnightLock vault",
            Filter = "KnightLock vault (*.vault)|*.vault|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true,
            InitialDirectory = SafeFolder(App.Current.Settings.LastVaultFolder)
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        int added = 0;
        string? firstPath = null;
        string? problem = null;

        foreach (string path in dialog.FileNames)
        {
            try
            {
                var header = VaultService.Inspect(path);
                App.Current.Registry.AddOrUpdate(new VaultRecord
                {
                    Id = header.VaultId,
                    Name = header.Name,
                    Path = path,
                    CreatedUtc = header.CreatedUtc,
                    SizeBytes = new System.IO.FileInfo(path).Length,
                    FileCount = header.FileCount,
                    SourceFolder = header.SourceFolder
                });

                firstPath ??= path;
                added++;
            }
            catch (Exception ex)
            {
                problem ??= VaultException.From(ex).Message;
            }
        }

        if (added > 0)
        {
            App.Current.Settings.LastVaultFolder = System.IO.Path.GetDirectoryName(firstPath!) ?? "";
            App.Current.Settings.Save();

            Refresh();
            _selected = _items.FirstOrDefault(i =>
                string.Equals(i.FullPath, firstPath, StringComparison.OrdinalIgnoreCase));
            ApplySelection();

            if (added > 1)
                await App.Shell.ShowAsync("Vaults added",
                    $"{added} vaults were added to your library.",
                    "OK", closeText: null, severity: DialogSeverity.Success);
        }
        else if (problem is not null)
        {
            await App.Shell.ShowAsync("Could not add that file", problem,
                "OK", closeText: null, severity: DialogSeverity.Error);
        }
    }

    private void NewVault_Click(object sender, RoutedEventArgs e)
        => ((MainWindow)Window.GetWindow(this)).NavigateToCreateVault();

    private async void RemoveFromLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;

        var answer = await App.Shell.ShowAsync(
            "Remove from library?",
            $"\"{_selected.Name}\" will be removed from KnightLock's list.\n\n" +
            "The .vault file itself is NOT deleted and can be added back at any time.",
            "Remove",
            closeText: "Cancel",
            severity: DialogSeverity.Warning);

        if (answer != DialogResultKind.Primary) return;

        if (App.Current.Sessions.IsUnlocked(_selected.FullPath))
            await App.Current.Sessions.LockAsync(_selected.FullPath, sealChanges: true);

        App.Current.Registry.Remove(_selected.FullPath);
        _selected = null;
        Refresh();
    }

    // ------------------------------------------------------------------ helpers

    private void OnAutoLocked(object? sender, AutoLockEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            Refresh();

            if (!IsVisible) return;

            _ = App.Shell.ShowAsync("Vault auto-locked",
                $"\"{e.Session.DisplayName}\" was locked automatically after {App.Current.Settings.AutoLockMinutes} minutes. " +
                "Changes were sealed back into the container and the decrypted copy was wiped.",
                "OK", closeText: null, severity: DialogSeverity.Info);
        });
    }

    private CancellationTokenSource BeginBusy(string title, string message, bool allowCancel)
    {
        var cts = new CancellationTokenSource();

        if (allowCancel)
        {
            _busyCancelHandler = (_, _) =>
            {
                if (!cts.IsCancellationRequested) cts.Cancel();
            };
            App.Shell.CancelRequested += _busyCancelHandler;
        }

        App.Shell.ShowBusy(title, message, determinate: true, allowCancel: allowCancel);
        return cts;
    }

    private void EndBusy()
    {
        if (_busyCancelHandler is not null)
        {
            App.Shell.CancelRequested -= _busyCancelHandler;
            _busyCancelHandler = null;
        }
        App.Shell.HideBusy();
    }

    private async Task ShowVaultErrorAsync(Exception ex)
    {
        var vex = VaultException.From(ex);
        Log.Error($"Vault operation failed ({vex.Error})", ex);

        await App.Shell.ShowAsync(vex.Title, vex.Message, "OK", closeText: null,
            severity: vex.Error == VaultError.WrongPassword ? DialogSeverity.Warning : DialogSeverity.Error);
    }

    private static string SafeFolder(string path)
        => !string.IsNullOrWhiteSpace(path) && System.IO.Directory.Exists(path)
            ? path
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
}

/// <summary>View model for one card in the vault list.</summary>
public sealed class VaultCardItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isUnlocked;

    public VaultCardItem(VaultRecord record) => Record = record;

    public VaultRecord Record { get; }

    public string FullPath => Record.Path;
    public string Name => string.IsNullOrWhiteSpace(Record.Name)
        ? System.IO.Path.GetFileNameWithoutExtension(Record.Path)
        : Record.Name;

    public string Path => FullPath;

    public string SizeText => ByteSizeText.Format(Record.SizeBytes);
    public string FilesText => $"{Record.FileCount:N0}";

    public string MetaText
    {
        get
        {
            string size = Record.SizeBytes > 0 ? ByteSizeText.Format(Record.SizeBytes) : "unknown size";
            string files = Record.FileCount > 0 ? $"{Record.FileCount:N0} files" : "";
            string when = Record.LastOpenedUtc is DateTime opened
                ? $"opened {RelativeTimeText.Format(opened)}"
                : $"created {Record.CreatedUtc.ToLocalTime():d MMM yyyy}";

            return string.Join(" · ", new[] { size, files, when }.Where(s => !string.IsNullOrEmpty(s)));
        }
    }

    public bool IsUnlocked
    {
        get => _isUnlocked;
        set
        {
            if (_isUnlocked == value) return;
            _isUnlocked = value;
            Raise(nameof(IsUnlocked));
            Raise(nameof(StatusText));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            Raise(nameof(IsSelected));
        }
    }

    public string StatusText => IsUnlocked ? "Unlocked" : "Locked";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

internal static class ByteSizeText
{
    public static string Format(long bytes) => Converters.ByteSizeConverter.Format(bytes);
}

internal static class RelativeTimeText
{
    public static string Format(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        return span switch
        {
            { TotalSeconds: < 60 } => "just now",
            { TotalMinutes: < 60 } => $"{(int)span.TotalMinutes}m ago",
            { TotalHours: < 24 } => $"{(int)span.TotalHours}h ago",
            { TotalDays: < 7 } => $"{(int)span.TotalDays}d ago",
            _ => utc.ToLocalTime().ToString("d MMM yyyy")
        };
    }
}
