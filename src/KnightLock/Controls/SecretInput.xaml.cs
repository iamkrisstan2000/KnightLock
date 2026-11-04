using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;

namespace KnightLock.Controls;

/// <summary>
/// WinUI-style secret field: masked by default, with an explicit reveal toggle.
/// Focus and caret are carried across the mask/reveal swap so typing is never interrupted.
/// </summary>
public partial class SecretInput : UserControl
{
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.Register(
        nameof(Password), typeof(string), typeof(SecretInput),
        new FrameworkPropertyMetadata(string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPasswordChangedFromBinding));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(SecretInput),
        new PropertyMetadata("Password", OnPlaceholderChanged));

    public SecretInput()
    {
        InitializeComponent();

        IsKeyboardFocusWithinChanged += (_, _) => UpdateChrome();

        // Clicking anywhere on the field surface (including its padding, or the reveal button)
        // should put the caret in the input, the way a native password box behaves.
        Chrome.MouseLeftButtonDown += (_, _) => FocusInput();

        UpdateChrome();
        UpdateWatermark();
    }

    /// <summary>Focus cue for the field surface: the WinUI accent underline, nothing heavier.</summary>
    private void UpdateChrome()
    {
        if (FocusLine is null) return;
        FocusLine.Visibility = IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => (d as SecretInput)?.UpdateWatermark();

    /// <summary>
    /// WPF's PasswordBox has no bindable Text, so the watermark is pushed into the
    /// control's Tag (which the template renders) and cleared the moment text exists.
    /// </summary>
    private void UpdateWatermark()
    {
        Masked.Tag = Masked.Password.Length == 0 ? Placeholder : string.Empty;
        Revealed.Tag = Revealed.Text.Length == 0 ? Placeholder : string.Empty;
    }

    /// <summary>Raised whenever the user edits the secret (never on programmatic set).</summary>
    public event EventHandler? PasswordChanged;

    /// <summary>Raised when Enter is pressed inside the field.</summary>
    public event EventHandler? Submitted;

    public string Password
    {
        get => (string)GetValue(PasswordProperty);
        set => SetValue(PasswordProperty, value);
    }

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public void Clear()
    {
        Masked.Clear();
        Revealed.Clear();
        SetValue(PasswordProperty, string.Empty);
        UpdateWatermark();
    }

    public void FocusInput()
    {
        if (Revealed.Visibility == Visibility.Visible) Revealed.Focus();
        else Masked.Focus();
    }

    private static void OnPasswordChangedFromBinding(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SecretInput input) return;
        string value = e.NewValue as string ?? "";

        if (input.Masked.Password != value) input.Masked.Password = value;
        if (input.Revealed.Text != value) input.Revealed.Text = value;
        input.UpdateWatermark();
    }

    private void Masked_PasswordChanged(object sender, RoutedEventArgs e)
    {
        UpdateWatermark();
        if (Revealed.Visibility == Visibility.Visible) return;
        SetValue(PasswordProperty, Masked.Password);
        PasswordChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Revealed_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateWatermark();
        if (Revealed.Visibility != Visibility.Visible) return;
        SetValue(PasswordProperty, Revealed.Text);
        PasswordChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RevealButton_Click(object sender, RoutedEventArgs e)
    {
        bool revealing = Revealed.Visibility != Visibility.Visible;

        if (revealing)
        {
            Revealed.Text = Masked.Password;
            Masked.Visibility = Visibility.Collapsed;
            Revealed.Visibility = Visibility.Visible;
            SetRevealed(true);
            Revealed.Focus();
            Revealed.CaretIndex = Revealed.Text.Length;
        }
        else
        {
            Masked.Password = Revealed.Text;
            Revealed.Visibility = Visibility.Collapsed;
            Masked.Visibility = Visibility.Visible;
            SetRevealed(false);
            Masked.Focus();
        }

        UpdateWatermark();
    }

    /// <summary>Swaps the reveal glyph (eye = hidden, eye with slash = visible) and its tooltip.</summary>
    private void SetRevealed(bool revealed)
    {
        RevealGlyph.Data = (System.Windows.Media.Geometry)FindResource(revealed ? "GeoEyeOff" : "GeoEye");
        RevealButton.ToolTip = revealed ? "Hide password" : "Show password";
        System.Windows.Automation.AutomationProperties.SetName(
            RevealButton, revealed ? "Hide password" : "Show password");
    }

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Submitted?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }
}
