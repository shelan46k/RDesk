using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using RDesk.Helpers;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace RDesk.Services;

/// <summary>
/// Light / Dark / System theme, accent colour and backdrop material. Brushes live in Application
/// resources and are mutated in place, so every StaticResource reference and code-built card updates instantly.
/// </summary>
public sealed class ThemeService
{
    public const string PanelBackgroundBrushKey = "PanelBackgroundBrush";
    public const string BoxBackgroundBrushKey = "BoxBackgroundBrush";
    public const string CardBackgroundBrushKey = "CardBackgroundBrush";
    public const string CardBorderBrushKey = "CardBorderBrush";
    public const string PillBackgroundBrushKey = "PillBackgroundBrush";
    public const string ItemHoverBrushKey = "ItemHoverBrush";
    public const string PrimaryTextBrushKey = "PrimaryTextBrush";
    public const string SecondaryTextBrushKey = "SecondaryTextBrush";
    public const string AccentTextBrushKey = "AccentTextBrush";
    public const string AccentFillBrushKey = "AccentFillBrush";
    public const string SegmentTrackBrushKey = "SegmentTrackBrush";
    public const string SegmentSelectedBrushKey = "SegmentSelectedBrush";
    public const string WarningTextBrushKey = "WarningTextBrush";
    public const string SuccessTextBrushKey = "SuccessTextBrush";
    public const string DangerTextBrushKey = "DangerTextBrush";
    public const string DialogPrimaryTextBrushKey = "DialogPrimaryTextBrush";
    public const string DialogSecondaryTextBrushKey = "DialogSecondaryTextBrush";

    public static readonly string[] AccentNames = ["Blue", "Purple", "Green", "Orange", "Pink"];

    private readonly ConfigService _config;
    private readonly UISettings _uiSettings = new();

    public event EventHandler? ThemeChanged;

    public ThemeService(ConfigService config)
    {
        _config = config;
        _uiSettings.ColorValuesChanged += (_, _) =>
        {
            if (IsSystemMode)
            {
                ThemeChanged?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    public bool IsSystemMode =>
        string.Equals(_config.Current.ThemeMode, "System", StringComparison.OrdinalIgnoreCase);

    public ElementTheme CurrentElementTheme
    {
        get
        {
            if (IsSystemMode)
            {
                var bg = _uiSettings.GetColorValue(UIColorType.Background);
                return bg.R < 128 ? ElementTheme.Dark : ElementTheme.Light;
            }

            return string.Equals(_config.Current.ThemeMode, "Dark", StringComparison.OrdinalIgnoreCase)
                ? ElementTheme.Dark
                : ElementTheme.Light;
        }
    }

    public bool IsDark => CurrentElementTheme == ElementTheme.Dark;

    /// <summary>
    /// Overlay (sidebar / boxes) should paint light glyphs when the wallpaper is dark
    /// and the glass is see-through, otherwise black text vanishes into the desktop.
    /// </summary>
    public bool OverlayUsesLightText => ContrastAmount > 0.5 && WallpaperLuminance.IsDark;

    /// <summary>0 = keep the theme palette, 1 = full wallpaper-contrast palette.</summary>
    public double ContrastAmount
    {
        get
        {
            if (!WallpaperLuminance.IsDark)
            {
                return 0;
            }

            var t = Math.Max(
                Math.Clamp(_config.Current.PanelTransparency, 0.0, 1.0),
                Math.Clamp(_config.Current.CardTransparency, 0.0, 1.0));
            return Smoothstep(0.28, 0.72, t);
        }
    }

    public static SolidColorBrush Brush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is SolidColorBrush b
            ? b
            : new SolidColorBrush(Color.FromArgb(0xFF, 0x80, 0x80, 0x80));

    public void ApplyTo(FrameworkElement root, bool overlay = true)
    {
        try
        {
            root.RequestedTheme = overlay && OverlayUsesLightText
                ? ElementTheme.Dark
                : CurrentElementTheme;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            // The hosting window was closed while an Activated/theme event was still queued.
            StartupLog.Write("ApplyTo skipped (window closed)", detail: ex.Message);
        }

        ApplyBrushes();
    }

    public enum ChromeKind
    {
        Panel,
        Box
    }

    /// <summary>Sets the configured backdrop material and DWM rounded corners on a RDesk surface.</summary>
    public void ApplyWindowChrome(Window window, ChromeKind kind = ChromeKind.Panel)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(window);
            DesktopPinHelper.ApplyRoundedCorners(hwnd);
            DesktopPinHelper.ExtendGlassFrame(hwnd);
            DesktopPinHelper.SetBorderColor(hwnd, OverlayUsesLightText || IsDark
                ? Color.FromArgb(0xFF, 0x3A, 0x40, 0x4E)
                : Color.FromArgb(0xFF, 0xE2, 0xE8, 0xF0));

            var transparency = Math.Clamp(
                kind == ChromeKind.Box ? _config.Current.CardTransparency : _config.Current.PanelTransparency,
                0.0, 1.0);
            var mode = _config.Current.Backdrop;
            var useMaterial = !string.Equals(mode, "Solid", StringComparison.OrdinalIgnoreCase);

            if (useMaterial)
            {
                if (window.SystemBackdrop is ConfigurableSystemBackdrop existing)
                {
                    existing.Update(mode, IsDark, transparency);
                }
                else
                {
                    var created = new ConfigurableSystemBackdrop();
                    created.Update(mode, IsDark, transparency);
                    window.SystemBackdrop = created;
                }
            }
            else
            {
                if (window.SystemBackdrop is ConfigurableSystemBackdrop existing)
                {
                    existing.Dispose();
                }

                window.SystemBackdrop = null;
            }
        }
        catch (Exception ex)
        {
            StartupLog.Write("ApplyWindowChrome failed", ex);
        }
    }

    public void ApplyBrushes()
    {
        var dark = IsDark;
        var solid = string.Equals(_config.Current.Backdrop, "Solid", StringComparison.OrdinalIgnoreCase);
        var panelT = Math.Clamp(_config.Current.PanelTransparency, 0.0, 1.0);
        var cardT = Math.Clamp(_config.Current.CardTransparency, 0.0, 1.0);
        var panelStrength = 1.0 - panelT;
        var cardStrength = 1.0 - cardT;
        var contrast = ContrastAmount;
        var lightTextOnGlass = contrast > 0.001 && WallpaperLuminance.IsDark;
        var darkChrome = dark || OverlayUsesLightText;
        // Acrylic/Mica already frost the wallpaper; keep the XAML fill light so TintOpacity is visible.
        var panelAlpha = (byte)Math.Round(panelStrength * (solid ? 255 : dark ? 48 : 32));
        var boxAlpha = (byte)Math.Round(cardStrength * (solid ? 255 : dark ? 48 : 32));
        var cardAlpha = (byte)Math.Round(cardStrength * 255);
        var chromeFloor = (byte)Math.Round(contrast * 0xC8);

        Color Fill(byte alpha, byte r, byte g, byte b) => Color.FromArgb(alpha, r, g, b);

        if (dark)
        {
            Set(PanelBackgroundBrushKey, Fill(panelAlpha, 0x14, 0x17, 0x1F));
            Set(BoxBackgroundBrushKey, Fill(boxAlpha, 0x14, 0x17, 0x1F));
            Set(CardBackgroundBrushKey, Fill(cardAlpha, 0x26, 0x2B, 0x38));
        }
        else
        {
            Set(PanelBackgroundBrushKey, Fill(panelAlpha, 0xDC, 0xE8, 0xF5));
            Set(BoxBackgroundBrushKey, Fill(boxAlpha, 0xDC, 0xE8, 0xF5));
            Set(CardBackgroundBrushKey, Fill(cardAlpha, 0xFF, 0xFF, 0xFF));
        }

        // Buttons / tabs keep a readable plate once glyphs flip to light on a dark wallpaper.
        if (darkChrome)
        {
            Set(CardBorderBrushKey, Fill(Math.Max((byte)Math.Round(cardStrength * 0x2E), (byte)(contrast * 0x66)), 0xFF, 0xFF, 0xFF));
            Set(PillBackgroundBrushKey, Fill(Math.Max((byte)Math.Round(cardStrength * 0xB3), chromeFloor), 0x2A, 0x30, 0x3C));
            Set(ItemHoverBrushKey, Fill(Math.Max((byte)Math.Round(cardStrength * 0x1F), (byte)(contrast * 0x33)), 0xFF, 0xFF, 0xFF));
            Set(SegmentTrackBrushKey, Fill(Math.Max((byte)Math.Round(cardStrength * 0x66), chromeFloor), 0x0B, 0x0E, 0x14));
            Set(SegmentSelectedBrushKey, Fill(Math.Max(cardAlpha, Math.Max((byte)0xB3, chromeFloor)), 0x3A, 0x42, 0x55));
        }
        else
        {
            Set(CardBorderBrushKey, Fill((byte)Math.Round(cardStrength * 0xB3), 0xFF, 0xFF, 0xFF));
            Set(PillBackgroundBrushKey, Fill((byte)Math.Round(cardStrength * 0xE6), 0xFF, 0xFF, 0xFF));
            Set(ItemHoverBrushKey, Fill((byte)Math.Round(cardStrength * 0x80), 0xFF, 0xFF, 0xFF));
            Set(SegmentTrackBrushKey, Fill((byte)Math.Round(cardStrength * 0x14), 0x1A, 0x20, 0x2C));
            Set(SegmentSelectedBrushKey, Fill(Math.Max(cardAlpha, (byte)0xB3), 0xFF, 0xFF, 0xFF));
        }

        // Overlay glyphs follow the wallpaper once the glass is thin; dialogs keep the chosen theme.
        var overlayDarkChrome = dark || lightTextOnGlass;
        var primary = LerpRgb(
            Color.FromArgb(0xFF, 0x1A, 0x20, 0x2C),
            Color.FromArgb(0xFF, 0xF7, 0xFA, 0xFC),
            dark ? 1 : contrast);
        var secondary = LerpRgb(
            Color.FromArgb(0xFF, 0x4A, 0x55, 0x68),
            Color.FromArgb(0xFF, 0xA0, 0xAE, 0xC0),
            dark ? 1 : contrast);
        Set(PrimaryTextBrushKey, primary);
        Set(SecondaryTextBrushKey, secondary);
        Set(WarningTextBrushKey, LerpRgb(Color.FromArgb(0xFF, 0xDD, 0x6B, 0x20), Color.FromArgb(0xFF, 0xF6, 0xAD, 0x55), dark ? 1 : contrast));
        Set(SuccessTextBrushKey, LerpRgb(Color.FromArgb(0xFF, 0x2F, 0x85, 0x5A), Color.FromArgb(0xFF, 0x68, 0xD3, 0x91), dark ? 1 : contrast));
        Set(DangerTextBrushKey, LerpRgb(Color.FromArgb(0xFF, 0xC5, 0x30, 0x30), Color.FromArgb(0xFF, 0xFC, 0x81, 0x81), dark ? 1 : contrast));

        if (dark)
        {
            Set(DialogPrimaryTextBrushKey, Color.FromArgb(0xFF, 0xF7, 0xFA, 0xFC));
            Set(DialogSecondaryTextBrushKey, Color.FromArgb(0xFF, 0xA0, 0xAE, 0xC0));
        }
        else
        {
            Set(DialogPrimaryTextBrushKey, Color.FromArgb(0xFF, 0x1A, 0x20, 0x2C));
            Set(DialogSecondaryTextBrushKey, Color.FromArgb(0xFF, 0x4A, 0x55, 0x68));
        }

        var (accentLight, accentDark, gradFrom, gradTo) = AccentPalette(_config.Current.AccentColor);
        Set(AccentTextBrushKey, overlayDarkChrome ? accentDark : accentLight);
        Set(AccentFillBrushKey, overlayDarkChrome ? accentDark : gradTo);
        if (Application.Current.Resources.TryGetValue("LogoGradientBrush", out var g) && g is LinearGradientBrush logo &&
            logo.GradientStops.Count >= 2)
        {
            logo.GradientStops[0].Color = gradFrom;
            logo.GradientStops[1].Color = gradTo;
        }
    }

    public static (Color LightText, Color DarkText, Color GradientFrom, Color GradientTo) AccentPalette(string name) =>
        name switch
        {
            "Purple" => (Hex(0x6B46C1), Hex(0xB794F4), Hex(0xA78BFA), Hex(0x6B46C1)),
            "Green" => (Hex(0x2F855A), Hex(0x68D391), Hex(0x48BB78), Hex(0x2F855A)),
            "Orange" => (Hex(0xC05621), Hex(0xF6AD55), Hex(0xF6AD55), Hex(0xC05621)),
            "Pink" => (Hex(0xB83280), Hex(0xF687B3), Hex(0xF687B3), Hex(0xB83280)),
            _ => (Hex(0x2B6CB0), Hex(0x63B3ED), Hex(0x4FACFE), Hex(0x2B6CB0))
        };

    private static Color Hex(int rgb) =>
        Color.FromArgb(0xFF, (byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));

    private static double Smoothstep(double edge0, double edge1, double x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    private static Color LerpRgb(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return Color.FromArgb(
            0xFF,
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));
    }

    /// <summary>Header button: flips between explicit Light and Dark.</summary>
    public void Toggle()
    {
        var next = IsDark ? "Light" : "Dark";
        _config.Update(c => c.ThemeMode = next);
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetMode(string mode)
    {
        _config.Update(c => c.ThemeMode = mode);
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public static string TransparencyCaption(double transparency) =>
        $"{Math.Round(Math.Clamp(transparency, 0.0, 1.0) * 100)}%（0% 不透明，100% 全透明）";

    /// <summary>Accent / backdrop / opacity changed: re-apply to every surface.</summary>
    public void NotifyAppearanceChanged() => ThemeChanged?.Invoke(this, EventArgs.Empty);

    private static void Set(string key, Color color)
    {
        var resources = Application.Current.Resources;
        if (resources.TryGetValue(key, out var existing) && existing is SolidColorBrush brush)
        {
            brush.Color = color;
        }
        else
        {
            resources[key] = new SolidColorBrush(color);
        }
    }
}
