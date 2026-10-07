using System.Drawing.Drawing2D;
using System.Drawing.Text;
using EKrecorder.Diagnostics;

namespace EKrecorder.Shell;

/// <summary>
/// The Settings window's colours, as Windows 11 draws its own Settings: a light grey (or near-black) page with
/// slightly raised cards, the user's accent colour for what is chosen. Light or dark follows Windows.
/// </summary>
internal sealed class UiTheme
{
    private UiTheme(bool dark, Color accent)
    {
        Dark = dark;
        Accent = accent;
        if (dark)
        {
            Page = Hex(0x202020);
            Card = Hex(0x2B2B2B);
            CardBorder = Hex(0x1D1D1D);
            Text = Hex(0xFFFFFF);
            SecondaryText = Hex(0xC5C5C5);
            DisabledText = Hex(0x787878);
            Control = Hex(0x373737);
            ControlHover = Hex(0x3C3C3C);
            ControlPressed = Hex(0x323232);
            ControlBorder = Hex(0x444444);
            Rail = Hex(0x9A9A9A);
            Track = Hex(0x454545);
            AccentText = Hex(0x000000);
            Warning = Hex(0xFCE100);
            Error = Hex(0xFF99A4);
            InfoBar = Hex(0x442726);
        }
        else
        {
            Page = Hex(0xF3F3F3);
            Card = Hex(0xFBFBFB);
            CardBorder = Hex(0xE5E5E5);
            Text = Hex(0x1A1A1A);
            SecondaryText = Hex(0x5F5F5F);
            DisabledText = Hex(0xA0A0A0);
            Control = Hex(0xFDFDFD);
            ControlHover = Hex(0xF6F6F6);
            ControlPressed = Hex(0xF0F0F0);
            ControlBorder = Hex(0xD6D6D6);
            Rail = Hex(0x8A8A8A);
            Track = Hex(0xE0E0E0);
            AccentText = Hex(0xFFFFFF);
            Warning = Hex(0x9D5D00);
            Error = Hex(0xC42B1C);
            InfoBar = Hex(0xFDE7E9);
        }
    }

    public bool Dark { get; }

    public Color Page { get; }

    public Color Card { get; }

    public Color CardBorder { get; }

    public Color Text { get; }

    public Color SecondaryText { get; }

    public Color DisabledText { get; }

    /// <summary>A neutral button or switch background.</summary>
    public Color Control { get; }

    public Color ControlHover { get; }

    public Color ControlPressed { get; }

    public Color ControlBorder { get; }

    /// <summary>The part of a slider that is not chosen.</summary>
    public Color Rail { get; }

    /// <summary>A level meter's empty part.</summary>
    public Color Track { get; }

    public Color Accent { get; }

    /// <summary>Text on the accent colour.</summary>
    public Color AccentText { get; }

    public Color Warning { get; }

    public Color Error { get; }

    /// <summary>The "recording now" bar.</summary>
    public Color InfoBar { get; }

    /// <summary>The theme Windows uses for apps now (light or dark), with the user's accent colour.</summary>
    public static UiTheme Current()
    {
        bool dark = Application.IsDarkModeEnabled;
        return new UiTheme(dark, AccentColor(dark));
    }

    /// <summary>A mix of two colours (<paramref name="amountOfA"/> of the first).</summary>
    public static Color Blend(Color a, Color b, double amountOfA) => Color.FromArgb(
        (int)Math.Round((a.R * amountOfA) + (b.R * (1 - amountOfA))),
        (int)Math.Round((a.G * amountOfA) + (b.G * (1 - amountOfA))),
        (int)Math.Round((a.B * amountOfA) + (b.B * (1 - amountOfA))));

    /// <summary>
    /// The accent colour as Windows 11 uses it on controls: a darker shade on light, a lighter one on dark (so text
    /// on it stays readable). Windows' own blue when the accent cannot be read.
    /// </summary>
    private static Color AccentColor(bool dark)
    {
        try
        {
            var settings = new Windows.UI.ViewManagement.UISettings();
            Windows.UI.Color c = settings.GetColorValue(dark ? Windows.UI.ViewManagement.UIColorType.AccentLight2 : Windows.UI.ViewManagement.UIColorType.AccentDark1);
            return Color.FromArgb(c.R, c.G, c.B);
        }
        catch (Exception ex)
        {
            Log.Warn($"The Windows accent colour could not be read ({ex.Message}); using blue.");
            return dark ? Hex(0x60CDFF) : Hex(0x005FB8);
        }
    }

    private static Color Hex(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
}

/// <summary>The window's fonts at one scale (fonts are sized in pixels, so they follow the window's own scale).</summary>
internal sealed class UiFonts : IDisposable
{
    private static string? _family;

    private static (string Family, FontStyle Style)? _semibold;

    public UiFonts(float scale)
    {
        string family = Family;
        (string strongFamily, FontStyle strongStyle) = Semibold;
        Body = new Font(family, 14 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        Strong = new Font(strongFamily, 14 * scale, strongStyle, GraphicsUnit.Pixel);
        Caption = new Font(family, 12 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        CaptionStrong = new Font(strongFamily, 12 * scale, strongStyle, GraphicsUnit.Pixel);
        Heading = new Font(strongFamily, 15 * scale, strongStyle, GraphicsUnit.Pixel);
    }

    /// <summary>Windows 11's text font (Segoe UI Variable), else Segoe UI.</summary>
    public static string Family => _family ??= Installed("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";

    /// <summary>The semibold weight Windows 11 uses for headings (bold when no semibold face is installed).</summary>
    private static (string Family, FontStyle Style) Semibold => _semibold ??=
        Installed("Segoe UI Variable Text Semibold") ? ("Segoe UI Variable Text Semibold", FontStyle.Regular)
        : Installed("Segoe UI Semibold") ? ("Segoe UI Semibold", FontStyle.Regular)
        : (Family, FontStyle.Bold);

    public Font Body { get; }

    public Font Strong { get; }

    public Font Caption { get; }

    public Font CaptionStrong { get; }

    public Font Heading { get; }

    public void Dispose()
    {
        Body.Dispose();
        Strong.Dispose();
        Caption.Dispose();
        CaptionStrong.Dispose();
        Heading.Dispose();
    }

    internal static bool Installed(string family)
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Any(f => string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Icons from Windows' own icon font (Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10).</summary>
internal static class Glyphs
{
    public const string Monitor = "";
    public const string Video = "";
    public const string Folder = "";
    public const string Microphone = "";
    public const string Speaker = "";
    public const string Equalizer = "";
    public const string Keyboard = "";
    public const string Timer = "";
    public const string Power = "";
    public const string Play = "";
    public const string Warning = "";

    private static readonly Dictionary<int, Font> Fonts = new();
    private static string? _family;
    private static bool _looked;

    /// <summary>The icon font, or null when Windows has none (then no icons are drawn).</summary>
    public static string? Family
    {
        get
        {
            if (!_looked)
            {
                _looked = true;
                _family = new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" }.FirstOrDefault(UiFonts.Installed);
            }

            return _family;
        }
    }

    /// <summary>Draws <paramref name="glyph"/> centred in <paramref name="box"/>, <paramref name="pixels"/> high.</summary>
    public static void Draw(Graphics g, string glyph, Rectangle box, Color color, float pixels)
    {
        if (Family is not { } family)
        {
            return;
        }

        int key = (int)Math.Round(pixels * 10);
        if (!Fonts.TryGetValue(key, out Font? font))
        {
            font = new Font(family, pixels, FontStyle.Regular, GraphicsUnit.Pixel);
            Fonts[key] = font;
        }

        TextRenderer.DrawText(g, glyph, font, box, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }

    public static GraphicsPath Rounded(RectangleF bounds, float radius) => AppIcons.RoundedRectangle(bounds, radius);
}
