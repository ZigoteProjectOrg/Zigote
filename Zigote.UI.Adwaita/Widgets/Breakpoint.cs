namespace Zigote.UI.Adwaita;

/// <summary>Which measurement an <see cref="AdwBreakpointCondition" /> compares.</summary>
public enum AdwBreakpointConditionType
{
    MinWidth,
    MaxWidth,
    MinHeight,
    MaxHeight,
    MinAspectRatio,
    MaxAspectRatio,
}

/// <summary>
///     A condition on the size an <see cref="AdwBreakpointBin" /> was given — the Adwaita analogue
///     of a CSS media query. Build one with <see cref="MinWidth" /> and friends, or combine two with
///     <see cref="And" /> / <see cref="Or" />:
///     <c>AdwBreakpointCondition.MaxWidth(600).And(AdwBreakpointCondition.MinHeight(400))</c>.
/// </summary>
public sealed class AdwBreakpointCondition
{
    private readonly AdwBreakpointCondition? _lhs, _rhs;
    private readonly bool _requiresBoth;
    private readonly AdwBreakpointConditionType _type;
    private readonly float _value;

    private AdwBreakpointCondition(AdwBreakpointConditionType type, float value)
    {
        _type = type;
        _value = value;
    }

    private AdwBreakpointCondition(AdwBreakpointCondition lhs, AdwBreakpointCondition rhs,
        bool requiresBoth)
    {
        _lhs = lhs;
        _rhs = rhs;
        _requiresBoth = requiresBoth;
    }

    public static AdwBreakpointCondition MinWidth(float px)
    {
        return new AdwBreakpointCondition(AdwBreakpointConditionType.MinWidth, px);
    }

    public static AdwBreakpointCondition MaxWidth(float px)
    {
        return new AdwBreakpointCondition(AdwBreakpointConditionType.MaxWidth, px);
    }

    public static AdwBreakpointCondition MinHeight(float px)
    {
        return new AdwBreakpointCondition(AdwBreakpointConditionType.MinHeight, px);
    }

    public static AdwBreakpointCondition MaxHeight(float px)
    {
        return new AdwBreakpointCondition(AdwBreakpointConditionType.MaxHeight, px);
    }

    public static AdwBreakpointCondition MinAspectRatio(float ratio)
    {
        return new AdwBreakpointCondition(AdwBreakpointConditionType.MinAspectRatio, ratio);
    }

    public static AdwBreakpointCondition MaxAspectRatio(float ratio)
    {
        return new AdwBreakpointCondition(AdwBreakpointConditionType.MaxAspectRatio, ratio);
    }

    /// <summary>Both conditions must hold.</summary>
    public AdwBreakpointCondition And(AdwBreakpointCondition other)
    {
        return new AdwBreakpointCondition(this, other, true);
    }

    /// <summary>Either condition may hold.</summary>
    public AdwBreakpointCondition Or(AdwBreakpointCondition other)
    {
        return new AdwBreakpointCondition(this, other, false);
    }

    /// <summary>Does this condition hold at the given size?</summary>
    public bool Evaluate(Size size)
    {
        if (_lhs is not null && _rhs is not null)
            return _requiresBoth
                ? _lhs.Evaluate(size) && _rhs.Evaluate(size)
                : _lhs.Evaluate(size) || _rhs.Evaluate(size);

        // A zero-height box has no meaningful ratio; report false rather than dividing by it.
        var ratio = size.Height > 0f ? size.Width / size.Height : 0f;
        return _type switch {
            AdwBreakpointConditionType.MinWidth => size.Width >= _value,
            AdwBreakpointConditionType.MaxWidth => size.Width <= _value,
            AdwBreakpointConditionType.MinHeight => size.Height >= _value,
            AdwBreakpointConditionType.MaxHeight => size.Height <= _value,
            AdwBreakpointConditionType.MinAspectRatio => size.Height > 0f && ratio >= _value,
            _ => size.Height > 0f && ratio <= _value,
        };
    }

    public override string ToString()
    {
        if (_lhs is not null && _rhs is not null)
            return $"{_lhs} {(_requiresBoth ? "and" : "or")} {_rhs}";
        var name = _type switch {
            AdwBreakpointConditionType.MinWidth => "min-width",
            AdwBreakpointConditionType.MaxWidth => "max-width",
            AdwBreakpointConditionType.MinHeight => "min-height",
            AdwBreakpointConditionType.MaxHeight => "max-height",
            AdwBreakpointConditionType.MinAspectRatio => "min-aspect-ratio",
            _ => "max-aspect-ratio",
        };
        return $"{name}: {_value:0.##}";
    }
}

/// <summary>
///     A condition paired with what to do while it holds. <see cref="Apply" /> runs on the edge into
///     the breakpoint and <see cref="Unapply" /> on the edge out, so a breakpoint can set and then
///     restore state; a purely declarative layout swap needs neither and can use
///     <see cref="AdwBreakpointBin.Child" /> selection instead.
/// </summary>
public sealed class AdwBreakpoint(AdwBreakpointCondition condition)
{
    public AdwBreakpointCondition Condition { get; set; } = condition;

    /// <summary>Runs when the bin enters this breakpoint.</summary>
    public Action? Apply { get; set; }

    /// <summary>Runs when the bin leaves it.</summary>
    public Action? Unapply { get; set; }

    /// <summary>Widget shown while this breakpoint is the active one. Null keeps the bin's child.</summary>
    public Widget? Child { get; set; }
}

/// <summary>
///     AdwBreakpointBin — swaps what it shows based on the size it is given, rather than the size of
///     the window. Breakpoints are tested in order and the LAST matching one wins, which is what
///     lets you write them narrowest-first the way a mobile-first stylesheet reads.
///     <para>
///         This measures its own allocation, not the window: a bin inside a sidebar folds when the
///         sidebar is narrow, even on a wide display. That is the whole reason it exists alongside
///         <see cref="MediaQuery" />, which answers for the window.
///     </para>
/// </summary>
public sealed class AdwBreakpointBin : Widget
{
    private Widget? _active;
    private AdwBreakpoint? _activeBreakpoint;
    private Widget? _child;
    private Size _size;

    public AdwBreakpointBin(Widget? child = null)
    {
        _child = child;
    }

    /// <summary>Shown when no breakpoint matches.</summary>
    public Widget? Child
    {
        get => _child;
        set
        {
            if (ReferenceEquals(_child, value)) return;
            _child = value;
            MarkNeedsLayout();
        }
    }

    /// <summary>Breakpoints in narrowest-first order; the last match wins.</summary>
    public List<AdwBreakpoint> Breakpoints { get; } = [];

    /// <summary>The breakpoint currently applied, or null when the plain child is showing.</summary>
    public AdwBreakpoint? CurrentBreakpoint => _activeBreakpoint;

    public override Size Measure(Constraints c)
    {
        _size = c.Constrain(
            new Size(
                float.IsFinite(c.MaxWidth) ? c.MaxWidth : 0f,
                float.IsFinite(c.MaxHeight) ? c.MaxHeight : 0f
            )
        );

        // Last match wins, so a narrowest-first list reads top to bottom like a stylesheet.
        AdwBreakpoint? match = null;
        foreach (var bp in Breakpoints)
            if (bp.Condition.Evaluate(_size))
                match = bp;

        if (!ReferenceEquals(match, _activeBreakpoint))
        {
            _activeBreakpoint?.Unapply?.Invoke();
            _activeBreakpoint = match;
            match?.Apply?.Invoke();
        }

        var next = match?.Child ?? Child;
        if (!ReferenceEquals(next, _active))
        {
            // SwapChild keeps the outgoing subtree from staying mounted — it owns effects and
            // tickers that would otherwise run forever behind the visible one.
            SwapChild(_active, next);
            _active = next;
        }

        _active?.Measure(Constraints.Tight(_size.Width, _size.Height));
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );
        _active?.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        _active?.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return _active?.HitTest(point);
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return ChildOrEmpty(_active);
    }
}
