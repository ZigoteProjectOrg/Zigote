namespace Zigote.UI.Widgets;

public enum MainAxisAlignment
{
    Start,
    Center,
    End,
    SpaceBetween,
    SpaceAround,
    SpaceEvenly,
}

public enum CrossAxisAlignment
{
    Start,
    Center,
    End,
    Stretch,
}

public enum MainAxisSize
{
    Min,
    Max,
}

public enum HorizontalAlignment
{
    Left,
    Center,
    Right,
}

public enum VerticalAlignment
{
    Top,
    Center,
    Bottom,
}

/// <summary>A layout axis. Used by <c>Wrap</c> and other direction-aware widgets.</summary>
public enum Axis
{
    Horizontal,
    Vertical,
}

/// <summary>How a flex child consumes its allotted space.</summary>
public enum FlexFit
{
    /// <summary>Child may be smaller than its flex allotment (its measured size).</summary>
    Loose,

    /// <summary>Child is forced to exactly fill its flex allotment.</summary>
    Tight,
}
