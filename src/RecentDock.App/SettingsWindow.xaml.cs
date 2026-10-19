using System.Windows;
using System.Windows.Controls;
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

        StyleComboBox.SelectedIndex = (int)current.ThemeStyle;
        OpacitySlider.Value = current.PanelOpacity;
        FontSlider.Value = current.FontSize;
        IconSlider.Value = current.IconSize;
        ShowPathCheckBox.IsChecked = current.ShowFilePath;
        AlwaysOnTopCheckBox.IsChecked = current.AlwaysOnTop;
        EdgeSnapCheckBox.IsChecked = current.EnableEdgeSnap;
        EdgeAutoHideCheckBox.IsChecked = current.EnableEdgeAutoHide;
        AutoStartCheckBox.IsChecked = AutoStart.IsEnabled();

        UpdateValueLabels();

        _loading = false;
    }

    /// <summary>Current values as edited. Equal to the input when nothing was changed.</summary>
    public UiSettings Result { get; private set; }

    /// <summary>Exposed so the UI smoke test can hit test the controls.</summary>
    internal System.Windows.Controls.Slider OpacitySliderControl => OpacitySlider;

    internal System.Windows.Controls.Slider FontSliderControl => FontSlider;

    internal System.Windows.Controls.Slider IconSliderControl => IconSlider;

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        UpdateValueLabels();
        Publish();
    }

    /// <summary>
    /// Style change. The style decides whether the panel is translucent at all, so the
    /// opacity slider is disabled for the opaque style rather than left to do nothing.
    /// </summary>
    private void OnStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        UpdateOpacityAvailability();
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

    private void OnShowPathChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Publish();
    }

    private void OnAlwaysOnTopChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Publish();
    }

    private void OnEdgeBehaviorChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        // Auto-hide only makes sense when snapping can establish an edge.
        if (sender == EdgeSnapCheckBox && EdgeSnapCheckBox.IsChecked != true)
        {
            _loading = true;
            EdgeAutoHideCheckBox.IsChecked = false;
            _loading = false;
        }

        Publish();
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        bool wanted = AutoStartCheckBox.IsChecked == true;
        if (AutoStart.SetEnabled(wanted))
        {
            return;
        }

        _loading = true;
        AutoStartCheckBox.IsChecked = !wanted;
        _loading = false;

        MessageBox.Show(
            this,
            "无法修改开机自启动设置，注册表访问被拒绝。",
            "RecentDock",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
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
            ThemeStyle = SelectedStyle,
            PanelOpacity = Math.Round(OpacitySlider.Value, 2),
            FontSize = Math.Round(FontSlider.Value),
            IconSize = Math.Round(IconSlider.Value),
            ShowFilePath = ShowPathCheckBox.IsChecked == true,
            AlwaysOnTop = AlwaysOnTopCheckBox.IsChecked == true,
            EnableEdgeSnap = EdgeSnapCheckBox.IsChecked == true,
            EnableEdgeAutoHide = EdgeAutoHideCheckBox.IsChecked == true,
        };

        ThemeManager.Apply(Result);
        SettingsChanged?.Invoke(this, Result);
    }

    /// <summary>Currently selected style, tolerant of a not-yet-seeded combo box.</summary>
    private ThemeStyleKind SelectedStyle => StyleComboBox.SelectedIndex switch
    {
        1 => ThemeStyleKind.Glass,
        2 => ThemeStyleKind.Flat,
        _ => ThemeStyleKind.Classic,
    };

    /// <summary>
    /// The opacity slider has no effect on an opaque style, so it is disabled there
    /// rather than left draggable and doing nothing.
    /// </summary>
    private void UpdateOpacityAvailability()
    {
        bool translucent = SelectedStyle != ThemeStyleKind.Flat;

        OpacitySlider.IsEnabled = translucent;
        OpacityValueText.Text = translucent ? $"{OpacitySlider.Value * 100:F0}%" : "不透明";
        OpacityHintText.Text = translucent
            ? "向左更透（更能看到桌面），向右更实（文字更清楚）"
            : "扁平风格固定为不透明；切换回经典或玻璃可重新调整透明度";
    }

    private void UpdateValueLabels()
    {
        // Shown as a percentage because "0.7" means nothing to most users.
        UpdateOpacityAvailability();
        FontValueText.Text = $"{FontSlider.Value:F0} px";
        IconValueText.Text = $"{IconSlider.Value:F0} px";
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        // Seed from a fresh default rather than hard-coding values here, so the
        // defaults stay defined in exactly one place.
        var defaults = new UiSettings();

        _loading = true;
        StyleComboBox.SelectedIndex = (int)defaults.ThemeStyle;
        OpacitySlider.Value = defaults.PanelOpacity;
        FontSlider.Value = defaults.FontSize;
        IconSlider.Value = defaults.IconSize;
        ShowPathCheckBox.IsChecked = defaults.ShowFilePath;
        AlwaysOnTopCheckBox.IsChecked = defaults.AlwaysOnTop;
        EdgeSnapCheckBox.IsChecked = defaults.EnableEdgeSnap;
        EdgeAutoHideCheckBox.IsChecked = defaults.EnableEdgeAutoHide;
        _loading = false;

        UpdateValueLabels();
        Publish();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
