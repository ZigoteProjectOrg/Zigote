using Zigote.UI.Host;

namespace Zigote.UI.Widgets;

/// <summary>
///     Base class for widgets that manually implement layout, sizing, and painting.
/// </summary>
public abstract class RenderWidget : Widget
{
}

/// <summary>
///     Base class for widgets that do not have any child widgets.
/// </summary>
public abstract class LeafWidget : RenderWidget
{
    public override IEnumerable<Widget> GetChildren()
    {
        return [];
    }

    public override void Attach(App owner, Widget? parent)
    {
        Owner = owner;
        Parent = parent;
    }

    public override void Detach()
    {
        Owner = null;
        Parent = null;
    }
}