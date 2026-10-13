using System.Windows;
using System.Windows.Media;
using AvalonDock;
using AvalonDock.Themes;
using VsKeys = AvalonDock.Themes.VS2013.Themes.ResourceKeys;

namespace KspFlightPlanner.App;

/// <summary>
/// AvalonDock's Visual Studio 2013 theme, recoloured with the Windows 11 Fluent theme's own brushes so the docking frame
/// matches the rest of the app. The VS2013 dark/light theme underneath only supplies the button glyphs.
/// Call <see cref="Apply"/> again when Windows switches between light and dark: the brushes are copied, not referenced.
/// </summary>
public static class DockTheme
{
    private const string Accent = "accent";
    private const string Clear = "clear";

    // AvalonDock key → Fluent brush key (or Accent / Clear).
    private static readonly (object Key, string Fluent)[] Colours =
    [
        // Frame: window colour between panes, slightly lighter pane content
        (VsKeys.Background, "SolidBackgroundFillColorTertiaryBrush"),
        (VsKeys.TabBackground, "SolidBackgroundFillColorBaseBrush"),
        (VsKeys.PanelBorderBrush, "SolidBackgroundFillColorBaseBrush"),
        (VsKeys.ControlAccentBrushKey, Accent),
        (VsKeys.PreviewBoxBorderBrushKey, Accent),

        // Tool pane title bars: quiet; an accent grip marks the pane with focus
        (VsKeys.ToolWindowCaptionActiveBackground, "SolidBackgroundFillColorTertiaryBrush"),
        (VsKeys.ToolWindowCaptionActiveText, "TextFillColorPrimaryBrush"),
        (VsKeys.ToolWindowCaptionActiveGrip, Accent),
        (VsKeys.ToolWindowCaptionInactiveBackground, "SolidBackgroundFillColorTertiaryBrush"),
        (VsKeys.ToolWindowCaptionInactiveText, "TextFillColorSecondaryBrush"),
        (VsKeys.ToolWindowCaptionInactiveGrip, Clear),
        (VsKeys.ToolWindowCaptionButtonActiveGlyph, "TextFillColorSecondaryBrush"),
        (VsKeys.ToolWindowCaptionButtonInactiveGlyph, "TextFillColorTertiaryBrush"),
        (VsKeys.ToolWindowCaptionButtonActiveHoveredBackground, "SolidBackgroundFillColorQuarternaryBrush"),
        (VsKeys.ToolWindowCaptionButtonActiveHoveredBorder, "SolidBackgroundFillColorQuarternaryBrush"),
        (VsKeys.ToolWindowCaptionButtonActiveHoveredGlyph, "TextFillColorPrimaryBrush"),
        (VsKeys.ToolWindowCaptionButtonInactiveHoveredBackground, "SolidBackgroundFillColorQuarternaryBrush"),
        (VsKeys.ToolWindowCaptionButtonInactiveHoveredBorder, "SolidBackgroundFillColorQuarternaryBrush"),
        (VsKeys.ToolWindowCaptionButtonInactiveHoveredGlyph, "TextFillColorPrimaryBrush"),

        // Tabs along the bottom of a tool pane (e.g. Locations | Vessels)
        (VsKeys.ToolWindowTabSelectedActiveBackground, "SolidBackgroundFillColorTertiaryBrush"),
        (VsKeys.ToolWindowTabSelectedActiveText, Accent),
        (VsKeys.ToolWindowTabSelectedInactiveBackground, "SolidBackgroundFillColorTertiaryBrush"),
        (VsKeys.ToolWindowTabSelectedInactiveText, "TextFillColorPrimaryBrush"),
        (VsKeys.ToolWindowTabUnselectedBackground, "SolidBackgroundFillColorBaseBrush"),
        (VsKeys.ToolWindowTabUnselectedText, "TextFillColorSecondaryBrush"),
        (VsKeys.ToolWindowTabUnselectedHoveredBackground, "SolidBackgroundFillColorQuarternaryBrush"),
        (VsKeys.ToolWindowTabUnselectedHoveredText, "TextFillColorPrimaryBrush"),

        // Document tabs: the open tab joins the page below it; the accent marks the one with focus
        (VsKeys.DocumentWellTabSelectedActiveBackground, Accent),
        (VsKeys.DocumentWellTabSelectedActiveText, "TextOnAccentFillColorPrimaryBrush"),
        (VsKeys.DocumentWellTabSelectedInactiveBackground, "SolidBackgroundFillColorTertiaryBrush"),
        (VsKeys.DocumentWellTabSelectedInactiveText, "TextFillColorPrimaryBrush"),
        (VsKeys.DocumentWellTabUnselectedBackground, "SolidBackgroundFillColorBaseBrush"),
        (VsKeys.DocumentWellTabUnselectedText, "TextFillColorSecondaryBrush"),
        (VsKeys.DocumentWellTabUnselectedHoveredBackground, "SolidBackgroundFillColorQuarternaryBrush"),
        (VsKeys.DocumentWellTabUnselectedHoveredText, "TextFillColorPrimaryBrush"),
        (VsKeys.DocumentWellTabButtonSelectedActiveGlyph, "TextOnAccentFillColorPrimaryBrush"),
        (VsKeys.DocumentWellTabButtonSelectedInactiveGlyph, "TextFillColorSecondaryBrush"),
        (VsKeys.DocumentWellTabButtonUnselectedTabHoveredGlyph, "TextFillColorSecondaryBrush"),
        (VsKeys.DocumentWellOverflowButtonDefaultGlyph, "TextFillColorSecondaryBrush"),

        // Auto-hidden panes (pinned off to the edge)
        (VsKeys.AutoHideTabDefaultBackground, "SolidBackgroundFillColorBaseBrush"),
        (VsKeys.AutoHideTabDefaultBorder, "SolidBackgroundFillColorQuarternaryBrush"),
        (VsKeys.AutoHideTabDefaultText, "TextFillColorSecondaryBrush"),
        (VsKeys.AutoHideTabHoveredBackground, "SolidBackgroundFillColorBaseBrush"),
        (VsKeys.AutoHideTabHoveredBorder, Accent),
        (VsKeys.AutoHideTabHoveredText, "TextFillColorPrimaryBrush"),

        // Floating panes
        (VsKeys.FloatingDocumentWindowBackground, "SolidBackgroundFillColorBaseBrush"),
        (VsKeys.FloatingDocumentWindowBorder, Accent),
        (VsKeys.FloatingToolWindowBackground, "SolidBackgroundFillColorBaseBrush"),
        (VsKeys.FloatingToolWindowBorder, "SolidBackgroundFillColorQuarternaryBrush"),
    ];

    public static void Apply(DockingManager dock)
    {
        var app = Application.Current;
        var accent = app.TryFindResource(SystemColors.AccentColorBrushKey) as Brush ?? Brushes.DodgerBlue;
        foreach (var (key, fluent) in Colours)
        {
            dock.Resources[key] = fluent switch
            {
                Accent => accent,
                Clear => Brushes.Transparent,
                _ => app.TryFindResource(fluent) as Brush ?? Brushes.Gray,
            };
        }
        dock.Resources[VsKeys.PreviewBoxBackgroundBrushKey] = new SolidColorBrush((accent as SolidColorBrush)?.Color ?? Colors.DodgerBlue) { Opacity = 0.25 };

        var dark = app.TryFindResource("SolidBackgroundFillColorBaseBrush") is SolidColorBrush { Color: var c } && c.R + c.G + c.B < 3 * 128;
        // Our brushes sit directly in the manager's resources, so they win over the theme's merged-in dictionary.
        dock.Theme = dark ? new Vs2013DarkTheme() : new Vs2013LightTheme();
    }
}
