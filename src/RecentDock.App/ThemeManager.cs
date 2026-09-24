using System.Windows;
using System.Windows.Media;
using RecentDock.Core.Storage;

namespace RecentDock.App;

/// <summary>
/// Applies appearance settings to the running application.
///
/// Design decision: this MUTATES the existing brush objects in place rather than
/// replacing them in the resource dictionary.
///
/// Why: every brush in the panel is referenced with StaticResource, which resolves
/// once at load time and keeps a reference to the object it found. Swapping in a new
/// brush would leave every element still pointing at the old one, so nothing would
/// visibly change. Mutating Color on the brush the elements already hold makes them
/// repaint with no XAML changes. (DynamicResource would also work, but it costs a
/// lookup per property and would have to be applied to every reference.)
///
/// The styles are different: a Style cannot be mutated once sealed, so the text
/// styles ARE replaced. Those are consumed via StaticResource too, which means a
/// text-size change needs the affected controls to re-read the style. The panel does
/// that by reapplying its row styles on change (see MainWindow.ApplyFontScale).
/// </summary>
public static class ThemeManager
{
    /// <summary>Baseline alphas, matching Theme.xaml. Opacity scales these.</summary>
    private const byte TintBaseAlpha = 0xCC;
    private const byte HeaderBaseAlpha = 0xC0;
    private const byte SurfaceBaseAlpha = 0xB3;

    private static readonly Color LightSurface = Colors.White;
    private static readonly Color DarkSurface = Color.FromRgb(0x1A, 0x1B, 0x1F);

    /// <summary>
    /// Apply every appearance setting. Idempotent: each call recomputes from the
    /// baselines instead of accumulating.
    /// </summary>
    public static void Apply(UiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ApplyPalette(settings.PanelOpacity, settings.UseDarkTheme);
        SetIconSize(settings.IconSize);
        ApplyTextStyles(settings.FontSize);
    }

    /// <summary>
    /// Repaint the palette in place.
    ///
    /// Opacity multiplies the baseline alpha, so 1.0 reproduces the designed look and
    /// lower values thin all three surfaces by the same factor. Scaling rather than
    /// setting each independently preserves the designed relationship between them.
    /// </summary>
    private static void ApplyPalette(double opacity, bool darkTheme)
    {
        double factor = Math.Clamp(opacity, 0.3, 1.0);
        Color tint = darkTheme ? DarkSurface : LightSurface;

        MutateBrush("GlassTintBrush", tint, Scale(TintBaseAlpha, factor));
        MutateBrush("GlassHeaderBrush", tint, Scale(HeaderBaseAlpha, factor));
        MutateBrush("GlassSurfaceBrush", tint, Scale(SurfaceBaseAlpha, factor));

        // The hairline is not scaled with the surfaces: it is a separator, and
        // thinning it just makes it disappear.
        MutateBrush("GlassBorderBrush", Colors.White, darkTheme ? (byte)0x33 : (byte)0x59);

        // Text tracks the surface, otherwise a dark tint leaves dark text on a dark
        // background.
        MutateBrush(
            "PrimaryTextBrush",
            darkTheme ? Color.FromRgb(0xED, 0xEE, 0xF1) : Color.FromRgb(0x1C, 0x1C, 0x20),
            0xFF);

        MutateBrush(
            "SecondaryTextBrush",
            darkTheme ? Color.FromRgb(0xA2, 0xA6, 0xAE) : Color.FromRgb(0x5F, 0x60, 0x68),
            0xFF);

        MutateBrush(
            "WarningBrush",
            darkTheme ? Color.FromRgb(0xE8, 0xB2, 0x5C) : Color.FromRgb(0xB4, 0x65, 0x1A),
            0xFF);

        // Row states invert with the theme: darken on a light surface, lighten on a
        // dark one. Backwards makes a hovered row vanish.
        MutateBrush("HoverBrush", darkTheme ? Colors.White : Colors.Black, darkTheme ? (byte)0x1F : (byte)0x14);
        MutateBrush("SelectedBrush", Color.FromRgb(0x2F, 0x6F, 0xD8), 0x33);
    }

    /// <summary>
    /// Rebuild the four text styles at a new size.
    ///
    /// A single value drives all of them; the other sizes are derived by offset so
    /// their relationship to the body text stays stable at every setting.
    /// </summary>
    private static void ApplyTextStyles(double fontSize)
    {
        double size = Math.Clamp(fontSize, 10, 24);

        ReplaceTextStyle("BodyTextStyle", size);
        ReplaceTextStyle("RowTitleTextStyle", size);
        ReplaceTextStyle("TitleTextStyle", size + 1);
        ReplaceTextStyle("CaptionTextStyle", size - 2);

        // Bare double used by controls that cannot take a TextBlock style (buttons,
        // the checkbox). Replaced rather than mutated: a double is a value type, so
        // DynamicResource on the consumer picks up the new instance.
        ReplaceResource("CaptionFontSize", size - 2);
    }

    /// <summary>Latest icon size, remembered so a text-size change does not reset it.</summary>
    private static double _iconSize = 16;

    /// <summary>Set the row icon size. Called by the settings window for live preview.</summary>
    public static void SetIconSize(double size)
    {
        _iconSize = Math.Clamp(size, 12, 48);
        ReplaceResource("RowIconSize", _iconSize);
    }

    /// <summary>Rebuild a TextBlock style with a new FontSize, keeping its other setters.</summary>
    private static void ReplaceTextStyle(string styleKey, double size)
    {
        if (Application.Current?.TryFindResource(styleKey) is not Style existing)
        {
            return;
        }

        var rebuilt = new Style(existing.TargetType, existing.BasedOn);
        foreach (SetterBase setter in existing.Setters)
        {
            if (setter is Setter { Property: var property }
                && property == System.Windows.Controls.TextBlock.FontSizeProperty)
            {
                continue; // replaced below
            }

            rebuilt.Setters.Add(setter);
        }

        rebuilt.Setters.Add(new Setter(System.Windows.Controls.TextBlock.FontSizeProperty, size));
        rebuilt.Seal();

        ReplaceResource(styleKey, rebuilt);
    }

    /// <summary>
    /// Recolour a brush in place. Falls back to inserting a new brush when the key is
    /// missing, which keeps a mis-typed key from failing silently.
    /// </summary>
    private static void MutateBrush(string key, Color color, byte alpha)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush brush)
        {
            if (brush.IsFrozen)
            {
                // Cannot mutate a frozen brush - a declared one from a ResourceDictionary
                // is frozen only if explicitly marked so, but handle it defensively
                // rather than throwing at runtime.
                ReplaceResource(key, Freeze(new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B))));
                return;
            }

            brush.Color = Color.FromArgb(alpha, color.R, color.G, color.B);
            return;
        }

        ReplaceResource(key, Freeze(new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B))));
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Write a key into the application dictionary, which takes precedence over the
    /// merged dictionaries, and also into any merged dictionary that defines it so a
    /// later lookup by either path agrees.
    /// </summary>
    private static void ReplaceResource(object key, object value)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        foreach (ResourceDictionary dictionary in app.Resources.MergedDictionaries)
        {
            if (dictionary.Contains(key))
            {
                dictionary[key] = value;
            }
        }

        app.Resources[key] = value;
    }

    /// <summary>Multiply a baseline alpha, clamped into the valid byte range.</summary>
    private static byte Scale(byte baseAlpha, double factor)
        => (byte)Math.Clamp(baseAlpha * factor, 0, 255);
}
