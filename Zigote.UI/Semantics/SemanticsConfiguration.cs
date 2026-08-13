namespace Zigote.UI.Semantics;

/// <summary>
///     A mutable description a widget fills in <see cref="Widgets.Widget.DescribeSemantics" />. The
///     semantics builder hands a fresh instance to each widget, then promotes it to an immutable
///     <see cref="SemanticsNode" /> if the widget contributed anything (<see cref="HasContent" />).
///     A widget that leaves the configuration empty is "semantically transparent" — its children are
///     hoisted into its parent's node instead.
/// </summary>
public sealed class SemanticsConfiguration
{
    public SemanticsRole Role { get; set; } = SemanticsRole.None;

    /// <summary>The accessible name (announced first) — e.g. a button caption or a field's hint/label.</summary>
    public string? Label { get; set; }

    /// <summary>The accessible value — e.g. a text field's contents or a slider's reading.</summary>
    public string? Value { get; set; }

    /// <summary>A supplementary description / usage hint announced after the label.</summary>
    public string? Hint { get; set; }

    public SemanticsFlags Flags { get; set; }
    public SemanticsAction Actions { get; set; }

    /// <summary>
    ///     When true the widget's descendants are NOT walked for semantics — the widget is a leaf in the
    ///     accessibility tree even if it has child widgets. Composed controls set this so the decorative
    ///     label/box inside (e.g. a Button's inner <c>Label</c>) doesn't produce a duplicate node; the
    ///     control merges that text into its own <see cref="Label" /> instead.
    /// </summary>
    public bool IsLeaf { get; set; }

    /// <summary>True once the widget has set a role, any text, a flag, or an action.</summary>
    public bool HasContent =>
        Role != SemanticsRole.None || Label is not null || Value is not null || Hint is not null ||
        Flags != SemanticsFlags.None || Actions != SemanticsAction.None;

    // ── Fluent helpers (keep widget DescribeSemantics overrides terse) ──────────

    public SemanticsConfiguration AddFlag(SemanticsFlags flag, bool on = true)
    {
        if (on) Flags |= flag;
        else Flags &= ~flag;
        return this;
    }

    public SemanticsConfiguration AddAction(SemanticsAction action)
    {
        Actions |= action;
        return this;
    }
}
