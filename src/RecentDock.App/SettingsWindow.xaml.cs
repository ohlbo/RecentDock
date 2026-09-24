using System.Windows;
using RecentDock.Core.Storage;

namespace RecentDock.App;

/// <summary>
/// Appearance settings.
///
/// Every control applies its change immediately rather than waiting for an OK
/// button: opacity, text size and icon size are all things the user has to SEE to
/// judge, and a modal preview would defeat the purpose. "关闭" therefore only
/// dismisses; the changes are already live and already persisted.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly UiSettings _current;

    /// <summary>Suppresses change handling while the controls are being seeded.</summary>
    private bool _loading = true;

    /// <summary>
    /// Raised whenever a setting changes, so the host can persist it and update the
    /// panel. Carries the full settings object.
    /// </summary>
    public event EventHandler<UiSettings>? SettingsChanged;

    public SettingsWindow(UiSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);

        _current = current;

        // Seeded so the property is never null even if the dialog is dismissed without
        // touching anything.
        Result = current;

        InitializeComponent();

        OpacitySlider.Value = current.PanelOpacity;
        FontSlider.Value = current.FontSize;
        IconSlider.Value = current.IconSize;

        UpdateValueLabels();

        _loading = false;
    }

    /// <summary>Current values as edited. Equal to the input when nothing was changed.</summary>
    public UiSettings Result { get; private set; }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        UpdateValueLabels();
        Publish();
    }

    private void OnFontSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        UpdateValueLabels();
        Publish();
    }

    private void OnIconSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        UpdateValueLabels();
        Publish();
    }

    /// <summary>
    /// Build the new settings object, apply it live, then tell the host.
    ///
    /// The theme is applied here rather than by the host so the preview happens on
    /// the same dispatcher tick as the slider move, with no visible lag.
    /// </summary>
    private void Publish()
    {
        Result = _current with
        {
            PanelOpacity = Math.Round(OpacitySlider.Value, 2),
            FontSize = Math.Round(FontSlider.Value),
            IconSize = Math.Round(IconSlider.Value),
        };

        ThemeManager.Apply(Result);
        SettingsChanged?.Invoke(this, Result);
    }

    private void UpdateValueLabels()
    {
        // Shown as a percentage because "0.7" means nothing to most users.
        OpacityValueText.Text = $"{OpacitySlider.Value * 100:F0}%";
        FontValueText.Text = $"{FontSlider.Value:F0} px";
        IconValueText.Text = $"{IconSlider.Value:F0} px";
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        // Seed from a fresh default rather than hard-coding values here, so the
        // defaults stay defined in exactly one place.
        var defaults = new UiSettings();

        _loading = true;
        OpacitySlider.Value = defaults.PanelOpacity;
        FontSlider.Value = defaults.FontSize;
        IconSlider.Value = defaults.IconSize;
        _loading = false;

        UpdateValueLabels();
        Publish();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
