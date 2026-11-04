using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KnightLock.Services;

namespace KnightLock.Controls;

public enum DialogResultKind
{
    None,
    Primary,
    Secondary,
    Close
}

public enum DialogSeverity
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>
/// WinUI 3 style ContentDialog + progress overlay, hosted above the whole shell.
/// One instance lives in MainWindow; pages call <see cref="ShowAsync"/> /
/// <see cref="ShowBusy"/> through <c>App.Dialogs</c>.
/// </summary>
public partial class ContentDialogHost : UserControl
{
    private TaskCompletionSource<DialogResultKind>? _completion;
    private bool _busyVisible;

    public ContentDialogHost()
    {
        InitializeComponent();
        LayerRoot.PreviewKeyDown += LayerRoot_PreviewKeyDown;
    }

    /// <summary>Raised when the user asks to cancel the running operation.</summary>
    public event EventHandler? CancelRequested;

    public bool IsBusy => _busyVisible;
    public bool IsDialogOpen => DialogCard.Visibility == Visibility.Visible;

    // ------------------------------------------------------------------ dialogs

    public Task<DialogResultKind> ShowAsync(
        string title,
        string message,
        string primaryText = "OK",
        string? secondaryText = null,
        string? closeText = "Cancel",
        DialogSeverity severity = DialogSeverity.Info,
        UIElement? extraContent = null)
    {
        // A dialog replaces any queued dialog rather than stacking.
        _completion?.TrySetResult(DialogResultKind.None);
        _completion = new TaskCompletionSource<DialogResultKind>(TaskCreationOptions.RunContinuationsAsynchronously);

        TitleText.Text = title;
        MessageText.Text = message;
        MessageText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;

        PrimaryButton.Content = primaryText;
        PrimaryButton.Visibility = string.IsNullOrEmpty(primaryText) ? Visibility.Collapsed : Visibility.Visible;

        if (string.IsNullOrEmpty(secondaryText))
        {
            SecondaryButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            SecondaryButton.Content = secondaryText;
            SecondaryButton.Visibility = Visibility.Visible;
        }

        if (string.IsNullOrEmpty(closeText))
        {
            CloseButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            CloseButton.Content = closeText;
            CloseButton.Visibility = Visibility.Visible;
        }

        ExtraContent.Content = extraContent;

        (string geometryKey, string brushKey) = severity switch
        {
            DialogSeverity.Success => ("GeoCheckCircle", "SystemFillColorSuccessBrush"),
            DialogSeverity.Warning => ("GeoWarning", "SystemFillColorCautionBrush"),
            DialogSeverity.Error => ("GeoErrorCircle", "SystemFillColorCriticalBrush"),
            _ => ("GeoInfo", "AccentTextFillColorPrimaryBrush")
        };

        GlyphIcon.Data = (Geometry)FindResource(geometryKey);
        GlyphIcon.SetResourceReference(IconPresenter.ForegroundProperty, brushKey);
        GlyphBadge.SetResourceReference(Border.BackgroundProperty,
            severity == DialogSeverity.Info ? "BrandTintBrush" : "CardBackgroundFillColorSecondaryBrush");

        LayerRoot.Visibility = Visibility.Visible;
        BusyCard.Visibility = Visibility.Collapsed;
        DialogCard.Visibility = Visibility.Visible;

        AnimateIn(DialogCard);
        FadeScrim(0.0, 1.0);

        PrimaryButton.Focus();

        return _completion.Task;
    }

    public Task<DialogResultKind> ShowErrorAsync(string message, string title = "Something went wrong") =>
        ShowAsync(title, message, "OK", closeText: null, severity: DialogSeverity.Error);

    private void Primary_Click(object sender, RoutedEventArgs e) => Complete(DialogResultKind.Primary);

    private void Secondary_Click(object sender, RoutedEventArgs e) => Complete(DialogResultKind.Secondary);

    private void Close_Click(object sender, RoutedEventArgs e) => Complete(DialogResultKind.Close);

    private void Complete(DialogResultKind result)
    {
        if (_completion is null) return;

        var completion = _completion;
        _completion = null;

        AnimateOut(DialogCard, () =>
        {
            DialogCard.Visibility = Visibility.Collapsed;
            ExtraContent.Content = null;
            if (!_busyVisible) HideLayer();
        });

        completion.TrySetResult(result);
    }

    private void LayerRoot_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (IsDialogOpen) Complete(DialogResultKind.Close);
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ busy overlay

    public void ShowBusy(string title, string message = "", bool determinate = false, bool allowCancel = false)
    {
        _busyVisible = true;

        BusyTitle.Text = title;
        BusyMessage.Text = message;
        BusyPercent.Text = "";
        BusyPercent.Visibility = Visibility.Collapsed;
        BusyBar.Visibility = determinate ? Visibility.Visible : Visibility.Collapsed;
        BusyBar.IsIndeterminate = !determinate;
        BusyBar.Value = 0;
        BusyRing.Visibility = Visibility.Visible;
        BusyRing.IsActive = true;
        BusyCancel.Visibility = allowCancel ? Visibility.Visible : Visibility.Collapsed;

        LayerRoot.Visibility = Visibility.Visible;
        DialogCard.Visibility = Visibility.Collapsed;
        BusyCard.Visibility = Visibility.Visible;

        AnimateIn(BusyCard);
        FadeScrim(0.0, 1.0);
    }

    public void UpdateBusy(string message, double? percent = null)
    {
        if (!_busyVisible) return;

        if (!string.IsNullOrEmpty(message)) BusyMessage.Text = message;

        if (percent is null)
        {
            BusyBar.IsIndeterminate = true;
            BusyPercent.Visibility = Visibility.Collapsed;
        }
        else
        {
            BusyBar.IsIndeterminate = false;
            BusyBar.Value = Math.Clamp(percent.Value, 0, 100);
            BusyPercent.Text = $"{percent.Value:0}%";
            BusyPercent.Visibility = Visibility.Visible;
        }
    }

    public void HideBusy()
    {
        if (!_busyVisible) return;
        _busyVisible = false;

        BusyRing.IsActive = false;
        AnimateOut(BusyCard, () =>
        {
            BusyCard.Visibility = Visibility.Collapsed;
            if (!IsDialogOpen) HideLayer();
        });

        // The overlay only ever hides when a vault operation has finished, so this is the one place
        // that reliably knows the crypto buffers are dead and the peak heap can be handed back.
        MemoryTrimmer.Release();
    }

    private void BusyCancel_Click(object sender, RoutedEventArgs e)
    {
        BusyCancel.IsEnabled = false;
        BusyMessage.Text = "Cancelling…";
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ animation

    private void HideLayer()
    {
        FadeScrim(1.0, 0.0, () =>
        {
            if (!IsDialogOpen && !_busyVisible)
                LayerRoot.Visibility = Visibility.Collapsed;
        });
    }

    private static void AnimateIn(FrameworkElement element)
    {
        element.Opacity = 0;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });

        if (element.RenderTransform is ScaleTransform scale)
        {
            var grow = new DoubleAnimation(0.96, 1.0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
    }

    private static void AnimateOut(FrameworkElement element, Action onDone)
    {
        var fade = new DoubleAnimation(element.Opacity, 0, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) => onDone();
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void FadeScrim(double from, double to, Action? onDone = null)
    {
        var fade = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(150));
        if (onDone is not null) fade.Completed += (_, _) => onDone();
        Scrim.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
