using System.Windows.Media;

namespace RevitMcp.Bridge;

// Shared palette for every RevitMcp WPF surface (Activity pane, Settings dialog)
// so all bridge UI reads as one product in both Revit themes.
internal sealed record RevitPalette(
    Color Background,
    Color Surface,
    Color Section,
    Color Hover,
    Color Pressed,
    Color Text,
    Color SecondaryText,
    Color MutedText,
    Color Border,
    Color Accent,
    Color Success,
    Color Warning,
    Color Error)
{
    public static RevitPalette Dark { get; } = new(
        Color.FromRgb(42, 46, 54),
        Color.FromRgb(50, 55, 64),
        Color.FromRgb(55, 61, 71),
        Color.FromRgb(52, 66, 86),
        Color.FromRgb(38, 52, 70),
        Color.FromRgb(242, 244, 247),
        Color.FromRgb(191, 199, 210),
        Color.FromRgb(145, 156, 171),
        Color.FromRgb(76, 84, 96),
        Color.FromRgb(58, 124, 186),
        Color.FromRgb(67, 190, 105),
        Color.FromRgb(224, 168, 59),
        Color.FromRgb(220, 101, 101));

    public static RevitPalette Light { get; } = new(
        Color.FromRgb(243, 243, 243),
        Color.FromRgb(250, 250, 250),
        Color.FromRgb(229, 229, 229),
        Color.FromRgb(225, 237, 247),
        Color.FromRgb(207, 226, 241),
        Color.FromRgb(35, 38, 42),
        Color.FromRgb(75, 82, 90),
        Color.FromRgb(105, 115, 126),
        Color.FromRgb(198, 198, 198),
        Color.FromRgb(25, 118, 185),
        Color.FromRgb(46, 125, 50),
        Color.FromRgb(178, 106, 0),
        Color.FromRgb(179, 38, 30));
}
