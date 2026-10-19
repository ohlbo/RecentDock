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
    /// Everything that differs between the visual styles.
    ///
    /// Kept as data rather than a switch inside ApplyPalette: adding a style then means
    /// adding one instance, and the values that make a style recognisable (how much
    /// shows through, how round the corners are, which accent is used) sit together
    /// instead of being scattered through the painting code.
    /// </summary>
    private sealed record Recipe(
        bool Translucent,
        Color Tint,
        Color Header,
        Color Surface,
        Color Border,
        Color Accent,
        Color Hover,
        Color PrimaryText,
        Color SecondaryText,
        Color DialogSurface,
        Color InputBackground,
        Color InputBorder,
        double FrameRadius,
        double RowRadius,
        double BorderThickness,
        bool Shadow,
        double ShadowBlur,
        double ShadowOpacity,
        double TintScale,
        double HeaderScale,
        double SurfaceScale,
        byte BorderAlpha,
        byte HoverAlpha);

    private static Recipe RecipeFor(ThemeStyleKind style, bool dark) => style switch
    {
        // Heavier glass. The surfaces are pulled down hard (72/66/55% of the classic
        // alphas) so the DWM material actually reads through, the corners are rounder,
        // and the edge is tinted with the accent instead of plain white - that coloured
        // rim is what makes it read as glass rather than as a translucent white card.
        ThemeStyleKind.Glass => new Recipe(
            Translucent: true,
            Tint: dark ? Color.FromRgb(0x14, 0x15, 0x1B) : Color.FromRgb(0xF7, 0xF8, 0xFF),
            Header: dark ? Color.FromRgb(0x1C, 0x1D, 0x26) : Color.FromRgb(0xFF, 0xFF, 0xFF),
            Surface: dark ? Color.FromRgb(0x1A, 0x1B, 0x24) : Color.FromRgb(0xFF, 0xFF, 0xFF),
            Border: dark ? Color.FromRgb(0xB9, 0xB2, 0xFF) : Color.FromRgb(0x7A, 0x5C, 0xFF),
            Accent: Color.FromRgb(0x6E, 0x5B, 0xFF),
            Hover: dark ? Colors.White : Colors.Black,
            PrimaryText: dark ? Color.FromRgb(0xF2, 0xF3, 0xFA) : Color.FromRgb(0x1B, 0x1A, 0x2E),
            SecondaryText: dark ? Color.FromRgb(0xAF, 0xB2, 0xC6) : Color.FromRgb(0x62, 0x63, 0x7A),
            DialogSurface: dark ? Color.FromRgb(0x22, 0x23, 0x2C) : Color.FromRgb(0xF8, 0xF8, 0xFD),
            InputBackground: dark ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF),
            InputBorder: dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x24, 0x00, 0x00, 0x00),
            FrameRadius: 18,
            RowRadius: 10,
            BorderThickness: 1.5,
            Shadow: true,
            ShadowBlur: 30,
            ShadowOpacity: 0.34,
            TintScale: 0.72,
            HeaderScale: 0.66,
            SurfaceScale: 0.55,
            BorderAlpha: dark ? (byte)0x66 : (byte)0x8C,
            HoverAlpha: dark ? (byte)0x24 : (byte)0x10),

        // Flat. Opaque everywhere, small radii, a hairline border that does the
        // separating work that transparency does in the other styles, and no shadow.
        // The opacity preference is deliberately ignored here: an opaque style cannot
        // be half transparent without turning back into glass.
        ThemeStyleKind.Flat => new Recipe(
            Translucent: false,
            Tint: dark ? Color.FromRgb(0x1E, 0x1F, 0x24) : Color.FromRgb(0xFF, 0xFF, 0xFF),
            Header: dark ? Color.FromRgb(0x26, 0x27, 0x2D) : Color.FromRgb(0xF3, 0xF4, 0xF7),
            Surface: dark ? Color.FromRgb(0x1E, 0x1F, 0x24) : Color.FromRgb(0xFF, 0xFF, 0xFF),
            Border: dark ? Color.FromRgb(0x3A, 0x3C, 0x43) : Color.FromRgb(0xE1, 0xE3, 0xE9),
            Accent: Color.FromRgb(0x25, 0x63, 0xEB),
            Hover: dark ? Colors.White : Color.FromRgb(0x0F, 0x17, 0x2A),
            PrimaryText: dark ? Color.FromRgb(0xEC, 0xED, 0xF1) : Color.FromRgb(0x11, 0x18, 0x27),
            SecondaryText: dark ? Color.FromRgb(0xA0, 0xA4, 0xAD) : Color.FromRgb(0x6B, 0x72, 0x80),
            DialogSurface: dark ? Color.FromRgb(0x26, 0x27, 0x2D) : Color.FromRgb(0xFF, 0xFF, 0xFF),
            InputBackground: dark ? Color.FromRgb(0x2B, 0x2C, 0x33) : Color.FromRgb(0xF5, 0xF6, 0xF8),
            InputBorder: dark ? Color.FromRgb(0x3A, 0x3C, 0x43) : Color.FromRgb(0xE1, 0xE3, 0xE9),
            FrameRadius: 6,
            RowRadius: 4,
            BorderThickness: 1,
            Shadow: false,
            ShadowBlur: 0,
            ShadowOpacity: 0,
            TintScale: 1.0,
            HeaderScale: 1.0,
            SurfaceScale: 1.0,
            BorderAlpha: 0xFF,
            HoverAlpha: dark ? (byte)0x14 : (byte)0x0C),

        // Classic: the appearance shipped before styles existed, reproduced exactly.
        _ => new Recipe(
            Translucent: true,
            Tint: dark ? DarkSurface : LightSurface,
            Header: dark ? DarkSurface : LightSurface,
            Surface: dark ? DarkSurface : LightSurface,
            Border: Colors.White,
            Accent: Color.FromRgb(0x2F, 0x6F, 0xD8),
            Hover: dark ? Colors.White : Colors.Black,
            PrimaryText: dark ? Color.FromRgb(0xED, 0xEE, 0xF1) : Color.FromRgb(0x1C, 0x1C, 0x20),
            SecondaryText: dark ? Color.FromRgb(0xA2, 0xA6, 0xAE) : Color.FromRgb(0x5F, 0x60, 0x68),
            DialogSurface: dark ? Color.FromRgb(0x25, 0x26, 0x2A) : Color.FromRgb(0xF7, 0xF7, 0xF9),
            InputBackground: dark ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF),
            InputBorder: dark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x30, 0x00, 0x00, 0x00),
            FrameRadius: 12,
            RowRadius: 7,
            BorderThickness: 1,
            Shadow: true,
            ShadowBlur: 20,
            ShadowOpacity: 0.28,
            TintScale: 1.0,
            HeaderScale: 1.0,
            SurfaceScale: 1.0,
            BorderAlpha: dark ? (byte)0x33 : (byte)0x59,
            HoverAlpha: dark ? (byte)0x1F : (byte)0x14),
    };

    /// <summary>
    /// Apply every appearance setting. Idempotent: each call recomputes from the
    /// baselines instead of accumulating.
    /// </summary>
    public static void Apply(UiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Recipe recipe = RecipeFor(settings.ThemeStyle, settings.UseDarkTheme);

        // Only the panel surfaces become transparent. Text and icons stay fully
        // opaque, which is the behaviour users expect from a glass panel.
        ApplyPalette(settings, recipe);
        ApplyGeometry(recipe);
        SetIconSize(settings.IconSize);
        ApplyTextStyles(settings.FontSize);
        ReplaceResource(
            "FilePathVisibility",
            settings.ShowFilePath ? Visibility.Visible : Visibility.Collapsed);
    }

    /// <summary>
    /// Shadow parameters for the current style, so the window does not have to know
    /// which styles use one.
    /// </summary>
    public static (bool Enabled, double Blur, double Opacity) ShadowFor(UiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Recipe recipe = RecipeFor(settings.ThemeStyle, settings.UseDarkTheme);
        return (settings.ShowDropShadow && recipe.Shadow, recipe.ShadowBlur, recipe.ShadowOpacity);
    }

    /// <summary>
    /// Corner radii and edge weight. These live in resources for the same reason the
    /// brushes do: the XAML references them through DynamicResource, so a style change
    /// reaches elements that already exist.
    /// </summary>
    private static void ApplyGeometry(Recipe recipe)
    {
        double radius = recipe.FrameRadius;

        ReplaceResource("FrameCornerRadius", new CornerRadius(radius));
        ReplaceResource("FrameHeaderCornerRadius", new CornerRadius(radius, radius, 0, 0));
        ReplaceResource("FrameFooterCornerRadius", new CornerRadius(0, 0, radius, radius));
        ReplaceResource("RowCornerRadius", new CornerRadius(recipe.RowRadius));
        ReplaceResource("FrameBorderThickness", new Thickness(recipe.BorderThickness));
    }

    /// <summary>
    /// Repaint the palette. Opacity scales only the translucent background surfaces;
    /// foreground brushes remain fully opaque.
    /// </summary>
    private static void ApplyPalette(UiSettings settings, Recipe recipe)
    {
        // Flat declares itself opaque, so the opacity preference is ignored rather
        // than half-applied, which would quietly turn it back into glass. The alphas
        // are stated outright instead of scaled: a scale of 1.0 still yields the 0xCC
        // baseline, which is translucent.
        double factor = recipe.Translucent ? Math.Clamp(settings.PanelOpacity, 0.3, 1.0) : 1.0;

        byte tintAlpha = recipe.Translucent ? Scale(recipe.TintScale, TintBaseAlpha, factor) : (byte)0xFF;
        byte headerAlpha = recipe.Translucent ? Scale(recipe.HeaderScale, HeaderBaseAlpha, factor) : (byte)0xFF;
        byte surfaceAlpha = recipe.Translucent ? Scale(recipe.SurfaceScale, SurfaceBaseAlpha, factor) : (byte)0xFF;

        MutateBrush("GlassTintBrush", recipe.Tint, tintAlpha);
        MutateBrush("GlassHeaderBrush", recipe.Header, headerAlpha);
        MutateBrush("GlassSurfaceBrush", recipe.Surface, surfaceAlpha);
        MutateBrush("DialogSurfaceBrush", recipe.DialogSurface, 0xFF);

        // The hairline is a separator, so it is not scaled with the surfaces: thinning
        // it just makes it disappear. Flat uses it as its main structural element, so
        // there it is fully opaque.
        MutateBrush("GlassBorderBrush", recipe.Border, recipe.BorderAlpha);

        // Inputs. The stock WPF text box paints its own light surface regardless of
        // Background, so the template in Theme.xaml uses these two brushes explicitly -
        // without them a dark panel keeps a bright white search box.
        MutateBrush("InputBackgroundBrush", recipe.InputBackground, recipe.InputBackground.A);
        MutateBrush("InputBorderBrush", recipe.InputBorder, recipe.InputBorder.A);

        // Text tracks the surface, otherwise a dark tint leaves dark text on a dark
        // background.
        MutateBrush("PrimaryTextBrush", recipe.PrimaryText, 0xFF);
        MutateBrush("SecondaryTextBrush", recipe.SecondaryText, 0xFF);
        MutateBrush("AccentBrush", recipe.Accent, 0xFF);

        MutateBrush(
            "WarningBrush",
            settings.UseDarkTheme ? Color.FromRgb(0xE8, 0xB2, 0x5C) : Color.FromRgb(0xB4, 0x65, 0x1A),
            0xFF);

        // Row states invert with the theme: darken on a light surface, lighten on a
        // dark one. Backwards makes a hovered row vanish.
        MutateBrush("HoverBrush", recipe.Hover, recipe.HoverAlpha);
        MutateBrush("SelectedBrush", recipe.Accent, 0x33);
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
    /// Recolour a palette brush by REPLACING the resource.
    ///
    /// In-place mutation was tried first and does not work: the XAML compiler freezes
    /// Freezables declared in a ResourceDictionary (and po:Freeze="False" does not
    /// prevent it here), so brush.Color cannot be assigned. The UI smoke test reports
    /// each brush as frozen or mutable to make that visible rather than silent.
    ///
    /// Replacement only reaches elements if they reference the brush dynamically, so
    /// every palette reference in the XAML uses DynamicResource. That is what makes
    /// the opacity slider actually move something.
    /// </summary>
    private static void MutateBrush(string key, Color color, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();

        ReplaceResource(key, brush);
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

    /// <summary>Multiply a baseline alpha by a style scale and the opacity factor.</summary>
    private static byte Scale(double styleScale, byte baseAlpha, double opacityFactor)
        => (byte)Math.Clamp(baseAlpha * styleScale * opacityFactor, 0, 255);

    /// <summary>
    /// Report whether the palette brushes can be recoloured in place, for diagnostics.
    ///
    /// A frozen brush silently defeats in-place recolouring, and the fallback path
    /// (replacing the resource) does not reach elements that already resolved the
    /// brush via StaticResource. That combination is invisible: the setting appears to
    /// save but nothing changes on screen.
    /// </summary>
    public static string DescribeBrushState()
    {
        string[] keys =
        {
            "GlassTintBrush",
            "GlassHeaderBrush",
            "GlassSurfaceBrush",
            "PrimaryTextBrush",
            "HoverBrush",
        };

        var parts = new List<string>(keys.Length);
        foreach (string key in keys)
        {
            if (Application.Current?.TryFindResource(key) is SolidColorBrush brush)
            {
                parts.Add($"{key}={(brush.IsFrozen ? "frozen" : "mutable")}");
            }
            else
            {
                parts.Add($"{key}=missing");
            }
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Current alpha of each translucent surface, for diagnostics.
    ///
    /// This is the value that decides what the window actually paints, so asserting
    /// it is the only way to prove the opacity setting has an effect. Everything else
    /// can report success while the pixels never change.
    /// </summary>
    public static string DescribeSurfaceAlphas()
    {
        string[] keys = { "GlassTintBrush", "GlassHeaderBrush", "GlassSurfaceBrush" };

        var parts = new List<string>(keys.Length);
        foreach (string key in keys)
        {
            if (Application.Current?.TryFindResource(key) is SolidColorBrush brush)
            {
                parts.Add($"{key.Replace("Glass", string.Empty).Replace("Brush", string.Empty)}=0x{brush.Color.A:X2}");
            }
            else
            {
                parts.Add($"{key}=missing");
            }
        }

        return string.Join(", ", parts);
    }
}
