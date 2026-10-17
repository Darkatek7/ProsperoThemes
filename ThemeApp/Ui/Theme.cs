using SharpProspero.Graphics;

namespace ProsperoThemes.App.Ui;

/// <summary>
/// The design tokens of the app: colours, spacing, radii and the type scale. Screens take their values from here
/// instead of repeating literals, so one change reaches every screen. All coordinates are for the 1920x1080 canvas.
/// </summary>
public static class Theme
{
    // ---- Colours ------------------------------------------------------------------------------------------------

    /// <summary>Near-black blue behind everything; also the colour the backdrop dims toward.</summary>
    public static readonly Color Ink = Color.FromRgb(7, 12, 21);
    /// <summary>Primary text and the strongest icons.</summary>
    public static readonly Color White = Color.FromRgb(242, 246, 253);
    /// <summary>Secondary text, inactive tabs, hint labels.</summary>
    public static readonly Color Muted = Color.FromRgb(154, 169, 190);
    /// <summary>Section labels, links, the selected tab underline, the busy dot.</summary>
    public static readonly Color Accent = Color.FromRgb(155, 209, 255);
    /// <summary>Text and symbols on a white or accent pill.</summary>
    public static readonly Color PillInk = Color.FromRgb(12, 18, 28);
    /// <summary>Updates, notices and anything that wants a second look without being an error.</summary>
    public static readonly Color Warn = Color.FromRgb(255, 190, 140);
    /// <summary>An idle key on the on-screen keyboard.</summary>
    public static readonly Color KeyFill = Color.FromRgb(30, 40, 54);
    /// <summary>Glass panel fill: Ink at about 80 percent, so a bright cover behind it still lets text stay readable.</summary>
    public static readonly Color Panel = Ink.WithAlpha(205);
    /// <summary>The faint 1 px edge of a glass panel.</summary>
    public static readonly Color PanelStroke = White.WithAlpha(30);
    /// <summary>Soft red for failures and destructive actions.</summary>
    public static readonly Color Danger = Color.FromRgb(255, 138, 138);
    /// <summary>Soft green for success.</summary>
    public static readonly Color Success = Color.FromRgb(144, 226, 172);
    /// <summary>The halo around a focused element, at its strongest; <see cref="Paint.FocusRing"/> fades it outwards.</summary>
    public static readonly Color FocusGlow = Accent.WithAlpha(96);
    /// <summary>Thin separators between rows and above the footer.</summary>
    public static readonly Color Hairline = Color.FromRgb(42, 53, 69);
    /// <summary>A pill that cannot be used right now.</summary>
    public static readonly Color PillOff = Color.FromRgb(55, 70, 89);

    // ---- Spacing (a grid of 8) ----------------------------------------------------------------------------------

    public const int ScreenWidth = 1920, ScreenHeight = 1080;
    /// <summary>The grid unit; paddings and gaps are multiples of it.</summary>
    public const int Unit = 8;
    /// <summary>Left and right margin (5 percent of the width, the TV safe area).</summary>
    public const int SafeX = 96;
    /// <summary>Top margin for the header row.</summary>
    public const int SafeTop = 56;
    /// <summary>Bottom margin below the footer row.</summary>
    public const int SafeBottom = 56;
    /// <summary>Right edge of the content column: <see cref="SafeX"/> mirrored.</summary>
    public const int ContentRight = ScreenWidth - SafeX;
    /// <summary>Width between the safe margins.</summary>
    public const int ContentWidth = ContentRight - SafeX;
    /// <summary>Y of the rule under the header.</summary>
    public const int HeaderRuleY = 144;
    /// <summary>First y a screen may draw content at, below the header rule.</summary>
    public const int ContentTop = 176;
    /// <summary>Last y (exclusive) a screen may draw content to, above the footer row.</summary>
    public const int ContentBottom = 944;
    /// <summary>Top of the footer row of controller hints.</summary>
    public const int FooterY = 984;
    /// <summary>Height of the footer row; it ends on the bottom safe line.</summary>
    public const int FooterHeight = ScreenHeight - SafeBottom - FooterY;

    // ---- Radii --------------------------------------------------------------------------------------------------

    /// <summary>Thumbnails and cards.</summary>
    public const int RadiusCard = 16;
    /// <summary>Glass panels.</summary>
    public const int RadiusPanel = 20;
    /// <summary>On-screen keyboard keys and the text box.</summary>
    public const int RadiusKey = 14;
    /// <summary>A pill is as round as it gets: half its height.</summary>
    public static int RadiusPill(int height) => height / 2;

    // ---- Type scale ---------------------------------------------------------------------------------------------

    /// <summary>One step of the type scale: the pixel size and weight of an Inter atlas and the file that holds it.</summary>
    public readonly record struct TypeStyle(string Name, int Size, int Weight, string File);

    /// <summary>
    /// The six text styles. Every one is a pre-baked atlas (the console has no font engine); a style is only
    /// usable when <c>tools/bake-assets.py</c> has produced its file and the build ships it.
    /// </summary>
    public static class TypeScale
    {
        /// <summary>The big screen title.</summary>
        public static readonly TypeStyle Hero = new("Hero", 76, 600, "inter-76.p5font");
        /// <summary>A screen heading below the hero, or the title of a dialog.</summary>
        public static readonly TypeStyle Heading = new("Heading", 48, 600, "inter-48.p5font");
        /// <summary>A section title.</summary>
        public static readonly TypeStyle Section = new("Section", 36, 600, "inter-36.p5font");
        /// <summary>Running text.</summary>
        public static readonly TypeStyle Body = new("Body", 28, 400, "inter-28.p5font");
        /// <summary>Buttons, tabs, card titles: body size with more weight.</summary>
        public static readonly TypeStyle Label = new("Label", 28, 600, "inter-28-semibold.p5font");
        /// <summary>Hints, metadata, small print.</summary>
        public static readonly TypeStyle Caption = new("Caption", 22, 400, "inter-22.p5font");
    }
}
