namespace Brezza.App.Native;

/// <summary>
/// Shared size tokens of the native shell, in unscaled pixels at 96 DPI; scale them with
/// <see cref="NativeMethods.Scale"/>. They are not part of a theme: a theme only changes colors.
/// </summary>
/// <remarks>
/// Only values that mean the same thing in more than one place belong here. A size that belongs to
/// one control's own layout (the tab-strip height, a panel's footer) stays a constant in that class.
/// </remarks>
internal static class NativeMetrics
{
    // ---------- Corner radii ----------

    /// <summary>Buttons, fields, list rows, hover and selection fills, pop-up rows.</summary>
    public const int RadiusControl = 4;

    /// <summary>The hover fill of an inactive tab.</summary>
    public const int RadiusTabHover = 6;

    /// <summary>The active tab, which merges into the toolbar below it.</summary>
    public const int RadiusTab = 8;

    // ---------- Spacing ----------

    /// <summary>Hairline gaps: between toolbar buttons, a pop-up and its anchor, list-row insets.</summary>
    public const int SpaceXSmall = 4;

    /// <summary>Gaps between related controls, such as text buttons in a row.</summary>
    public const int SpaceSmall = 8;

    /// <summary>Text padding inside rows and tabs.</summary>
    public const int SpaceMedium = 12;

    /// <summary>Insets of text blocks, such as the notice text.</summary>
    public const int SpaceLarge = 16;

    /// <summary>Margins of panels and separations between sections.</summary>
    public const int SpaceXLarge = 24;

    // ---------- Control sizes ----------

    /// <summary>Toolbar buttons, fields, and text buttons.</summary>
    public const int ControlHeight = 32;

    /// <summary>Compact text buttons, such as the notice actions, and small icon actions.</summary>
    public const int ControlHeightCompact = 28;

    /// <summary>The accent underline of a focused field.</summary>
    public const int FocusIndicatorThickness = 2;
}
