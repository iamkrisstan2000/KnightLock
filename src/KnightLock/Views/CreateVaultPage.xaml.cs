using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using KnightLock.Controls;
using KnightLock.Core;
using KnightLock.Services;

namespace KnightLock.Views;

public partial class CreateVaultPage : UserControl
{
    private string _sourceFolder = "";
    private string _destinationPath = "";
    private EventHandler? _busyCancelHandler;
    private int _estimatedFiles;
    private long _estimatedBytes;

    public CreateVaultPage()
    {
        InitializeComponent();
        UpdateEngineText();
        Validate();
    }

    public void OnNavigatedTo()
    {
        UpdateEngineText();
        Validate();
    }

    public void PrefillSource(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        SetSource(folder);
    }

    private void UpdateEngineText()
    {
        int iterations = App.Current?.Settings?.Iterations ?? VaultFormat.DefaultIterations;
        EngineText.Text = $"AES-256-GCM · PBKDF2-HMAC-SHA256 · {iterations:N0} rounds · 1 MiB authenticated chunks";
    }

    // ------------------------------------------------------------------ pickers

    private async void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the folder to lock",
            Multiselect = false,
            InitialDirectory = Directory.Exists(_sourceFolder)
                ? _sourceFolder
                : SafeFolder(App.Current.Settings.LastSourceFolder)
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        SetSource(dialog.FolderName);
        await SummarizeSourceAsync(dialog.FolderName);
    }

    private void SetSource(string folder)
    {
        _sourceFolder = folder;
        SourceField.Path = folder;

        if (string.IsNullOrWhiteSpace(_destinationPath))
        {
            string name = IOPath.SanitizeFolderName(new DirectoryInfo(folder).Name);
            string parent = Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar)) ?? folder;
            string baseFolder = SafeFolder(App.Current.Settings.LastVaultFolder, parent);
            DestinationField.Path = Path.Combine(baseFolder, name + VaultService.VaultExtension);
            _destinationPath = DestinationField.Path;
        }

        Validate();
    }

    private async Task SummarizeSourceAsync(string folder)
    {
        SourceSummary.Visibility = Visibility.Visible;
        SourceSummary.Text = "Counting files…";
        _estimatedFiles = 0;
        _estimatedBytes = 0;

        try
        {
            var (files, bytes) = await Task.Run(() =>
            {
                int count = 0;
                long size = 0;
                foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        size += new FileInfo(file).Length;
                        count++;
                    }
                    catch { }
                }
                return (count, size);
            });

            _estimatedFiles = files;
            _estimatedBytes = bytes;

            SourceSummary.Text = files == 0
                ? "That folder is empty — pick a folder that contains files."
                : $"{files:N0} files · {Converters.ByteSizeConverter.Format(bytes)} · will be compressed and encrypted";
        }
        catch (Exception ex)
        {
            SourceSummary.Text = $"Could not read the whole folder: {ex.Message}";
        }

        Validate();
    }

    private void BrowseDestination_Click(object sender, RoutedEventArgs e)
    {
        string suggested = string.IsNullOrWhiteSpace(_destinationPath)
            ? (string.IsNullOrWhiteSpace(_sourceFolder)
                ? "MyVault" + VaultService.VaultExtension
                : IOPath.SanitizeFolderName(new DirectoryInfo(_sourceFolder).Name) + VaultService.VaultExtension)
            : Path.GetFileName(_destinationPath);

        var dialog = new SaveFileDialog
        {
            Title = "Save the vault container",
            Filter = "KnightLock vault (*.vault)|*.vault",
            DefaultExt = VaultService.VaultExtension,
            AddExtension = true,
            FileName = suggested,
            InitialDirectory = SafeFolder(
                string.IsNullOrWhiteSpace(_destinationPath)
                    ? App.Current.Settings.LastVaultFolder
                    : Path.GetDirectoryName(_destinationPath) ?? "")
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        _destinationPath = VaultService.EnsureVaultExtension(dialog.FileName);
        DestinationField.Path = _destinationPath;
        Validate();
    }

    // ------------------------------------------------------------------ password

    private void PasswordBox_PasswordChanged(object sender, EventArgs e)
    {
        var assessment = PasswordStrength.Evaluate(PasswordBox.Password);

        var segments = new[] { Seg1, Seg2, Seg3, Seg4, Seg5 };
        string brushKey = assessment.Score switch
        {
            0 or 1 => "SystemFillColorCriticalBrush",
            2 => "SystemFillColorCautionBrush",
            3 or 4 => "SystemFillColorSuccessBrush",
            _ => "ControlStrokeColorDefaultBrush"
        };

        for (int i = 0; i < segments.Length; i++)
        {
            if (i <= assessment.Score && !string.IsNullOrEmpty(PasswordBox.Password))
                segments[i].SetResourceReference(Border.BackgroundProperty, brushKey);
            else
                segments[i].SetResourceReference(Border.BackgroundProperty, "ControlStrokeColorDefaultBrush");
        }

        if (string.IsNullOrEmpty(PasswordBox.Password))
        {
            StrengthLabel.Text = "";
            StrengthHint.Text = "";
        }
        else
        {
            StrengthLabel.Text = assessment.Label;
            StrengthLabel.SetResourceReference(TextBlock.ForegroundProperty, brushKey);

            bool mismatch = ConfirmBox.Password.Length > 0 && ConfirmBox.Password != PasswordBox.Password;
            StrengthHint.Text = mismatch ? "The passwords do not match yet." : assessment.Hint;
            StrengthHint.SetResourceReference(TextBlock.ForegroundProperty,
                mismatch ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
        }

        Validate();
    }

    private bool Validate()
    {
        if (CreateButton is null) return false;

        bool sourceOk = !string.IsNullOrWhiteSpace(_sourceFolder) && Directory.Exists(_sourceFolder);
        bool destinationOk = !string.IsNullOrWhiteSpace(_destinationPath);
        var assessment = PasswordStrength.Evaluate(PasswordBox.Password);
        bool passwordOk = assessment.MeetsMinimum;
        bool confirmOk = PasswordBox.Password.Length > 0 && PasswordBox.Password == ConfirmBox.Password;

        CreateButton.IsEnabled = sourceOk && destinationOk && passwordOk && confirmOk;
        return CreateButton.IsEnabled;
    }

    // ------------------------------------------------------------------ create

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (!Validate()) return;

        // Guard: replacing an existing container destroys data.
        if (File.Exists(_destinationPath))
        {
            var overwrite = await App.Shell.ShowAsync(
                "Replace the existing vault?",
                $"{Path.GetFileName(_destinationPath)} already exists in that folder.\n\n" +
                "Creating the new vault will permanently overwrite it. Any data it holds will be lost.",
                "Replace vault",
                closeText: "Cancel",
                severity: DialogSeverity.Warning);

            if (overwrite != DialogResultKind.Primary) return;
        }

        bool shredSource = ShredSourceCheck.IsChecked == true;
        if (shredSource)
        {
            var confirmShred = await App.Shell.ShowAsync(
                "Wipe the source folder afterwards?",
                $"KnightLock will create the vault, verify the container opens, and then permanently overwrite " +
                $"every file in:\n\n{_sourceFolder}\n\nThis cannot be undone.",
                "Create and wipe source",
                closeText: "Cancel",
                severity: DialogSeverity.Error);

            if (confirmShred != DialogResultKind.Primary) return;
        }

        string password = PasswordBox.Password;

        var cts = BeginBusy("Creating vault…", "Scanning folder…", allowCancel: true);
        var progress = new Progress<VaultProgress>(p => App.Shell.UpdateBusy(p.Message, p.Percent));

        try
        {
            var request = new CreateVaultRequest
            {
                SourceFolder = _sourceFolder,
                VaultFilePath = _destinationPath,
                Password = password,
                Iterations = App.Current.Settings.Iterations,
                Overwrite = true
            };

            var result = await VaultService.CreateAsync(request, progress, cts.Token);

            // Verify the container is readable with this password before claiming success.
            App.Shell.UpdateBusy("Verifying container…", null);
            bool verifies = await Task.Run(() => VaultService.VerifyPassword(result.VaultFilePath, password));

            if (!verifies)
                throw new VaultException(VaultError.Corrupt,
                    "The vault was written but could not be verified. Your source folder was left untouched.");

            App.Current.Registry.AddOrUpdate(new VaultRecord
            {
                Id = result.VaultId,
                Name = new DirectoryInfo(_sourceFolder).Name,
                Path = result.VaultFilePath,
                CreatedUtc = DateTime.UtcNow,
                SizeBytes = result.VaultBytes,
                FileCount = result.FileCount,
                SourceFolder = _sourceFolder
            });

            App.Current.Settings.LastSourceFolder = _sourceFolder;
            App.Current.Settings.LastVaultFolder = Path.GetDirectoryName(result.VaultFilePath) ?? "";
            App.Current.Settings.Save();

            EndBusy();

            string shredded = "";
            if (shredSource)
            {
                var shredBusy = BeginBusy("Wiping source folder…", "Overwriting original files with random data…", allowCancel: false);
                try
                {
                    await Task.Run(() => SecureDelete.WipeDirectory(_sourceFolder, App.Current.Settings.WipePasses));
                    shredded = "\n\nThe source folder has been overwritten and deleted.";
                }
                catch (Exception ex)
                {
                    shredded = $"\n\nSome source files could not be wiped: {ex.Message}";
                    Log.Error("Shredding the source folder failed", ex);
                }
                finally
                {
                    EndBusy();
                    shredBusy.Dispose();
                }
            }

            var answer = await App.Shell.ShowAsync(
                "Vault created",
                $"{result.FileCount:N0} files ({Converters.ByteSizeConverter.Format(result.SourceBytes)}) were " +
                $"compressed and encrypted into\n{Path.GetFileName(result.VaultFilePath)} " +
                $"({Converters.ByteSizeConverter.Format(result.VaultBytes)}, " +
                $"{result.CompressionRatio:P0} smaller) in {result.Duration.TotalSeconds:0.0}s.\n\n" +
                $"Skipped {result.Skipped.Count} item(s) that could not be read." + shredded,
                "Open folder",
                null,
                "Done",
                DialogSeverity.Success);

            ResetForm();

            if (answer == DialogResultKind.Primary)
                App.OpenInExplorer(Path.GetDirectoryName(result.VaultFilePath) ?? "");

            ((MainWindow)Window.GetWindow(this)).NavigateToVaults();
        }
        catch (OperationCanceledException)
        {
            EndBusy();
            await App.Shell.ShowAsync("Cancelled",
                "The vault was not created. Nothing was changed.",
                "OK", closeText: null, severity: DialogSeverity.Info);
        }
        catch (Exception ex)
        {
            EndBusy();
            var vex = VaultException.From(ex);
            Log.Error($"Vault creation failed ({vex.Error})", ex);
            await App.Shell.ShowAsync(vex.Title, vex.Message, "OK", closeText: null,
                severity: DialogSeverity.Error);
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => ResetForm();

    private void ResetForm()
    {
        _sourceFolder = "";
        _destinationPath = "";
        _estimatedFiles = 0;
        _estimatedBytes = 0;

        SourceField.Path = "";
        DestinationField.Path = "";
        SourceSummary.Visibility = Visibility.Collapsed;
        PasswordBox.Clear();
        ConfirmBox.Clear();
        ShredSourceCheck.IsChecked = false;

        foreach (var segment in new[] { Seg1, Seg2, Seg3, Seg4, Seg5 })
            segment.SetResourceReference(Border.BackgroundProperty, "ControlStrokeColorDefaultBrush");

        StrengthLabel.Text = "";
        StrengthHint.Text = "";
        Validate();
    }

    // ------------------------------------------------------------------ helpers

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

    private static string SafeFolder(string path, string? fallback = null)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) return path;
        if (fallback is not null && Directory.Exists(fallback)) return fallback;
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Directory.Exists(docs) ? docs : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}
