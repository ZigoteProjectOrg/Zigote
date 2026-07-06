namespace Zigote.UI.Theme;

/// <summary>
///     Standard control sizing, in logical pixels. macOS keeps controls compact and uniform — a
///     regular row is ~28 pt. Controls read these instead of hard-coding heights so the UI stays on
///     a consistent vertical rhythm.
/// </summary>
public static class ControlMetrics
{
    // ── Row / control heights ────────────────────────────────────────────────
    /// <summary>22 — dense toolbars, inline controls.</summary>
    public const float CompactHeight = 22f;

    /// <summary>28 — the default control height (buttons, fields, rows).</summary>
    public const float RegularHeight = 28f;

    /// <summary>36 — prominent/primary controls.</summary>
    public const float LargeHeight = 36f;

    // ── Discrete control sizes ───────────────────────────────────────────────
    public const float CheckboxSize = 16f;
    public const float RadioSize = 16f;

    public const float SwitchWidth = 38f;
    public const float SwitchHeight = 22f;

    public const float SliderTrack = 4f;
    public const float SliderThumb = 16f; // diameter

    // ── Chrome ───────────────────────────────────────────────────────────────
    public const float ToolbarHeight = 44f;
    public const float MenuRowHeight = 22f;

    /// <summary>Panel/section header strip (e.g. "Hierarchy", "Inspector").</summary>
    public const float PanelHeaderHeight = 28f;

    /// <summary>Dense list/tree row (asset rows, tree nodes).</summary>
    public const float RowHeight = 24f;

    /// <summary>Source-list / sidebar navigation row (roomier, selectable).</summary>
    public const float NavRowHeight = 32f;

    /// <summary>Inspector property row (label + control).</summary>
    public const float InspectorRowHeight = 28f;

    /// <summary>Minimum height of the bottom dock (project/console/timeline).</summary>
    public const float BottomPanelMinHeight = 160f;

    /// <summary>Smallest comfortable hit target.</summary>
    public const float MinHit = 20f;
}