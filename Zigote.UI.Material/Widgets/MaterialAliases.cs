namespace Zigote.UI.Material;

/// <summary>A hairline vertical separator (a vertical <see cref="Divider" />).</summary>
public sealed class VerticalDivider : StatelessWidget
{
    public VerticalDivider(double thickness = 1, double indent = 0, double endIndent = 0,
        Color? color = null)
    {
        Thickness = (float)thickness;
        Indent = (float)indent;
        EndIndent = (float)endIndent;
        Color = color;
    }

    public float Thickness { get; set; }
    public float Indent { get; set; }
    public float EndIndent { get; set; }
    public Color? Color { get; set; }

    protected override Widget Build(BuildContext context)
    {
        return new Divider(Thickness, Indent) {
            Vertical = true,
            EndIndent = EndIndent,
            Color = Color,
        };
    }
}

/// <summary>
///     A drag-to-reorder list. Alias over <see cref="ReorderableList" />. <c>onReorder</c> receives
///     <c>(oldIndex, newIndex)</c> where newIndex is the insertion slot before removal, so a handler
///     doing
///     <c>if (newIndex &gt; oldIndex) newIndex--</c> lands on the correct index.
/// </summary>
public sealed class ReorderableListView : StatelessWidget
{
    private readonly IList<Widget> _children;
    private readonly Action<int, int>? _onReorder;

    public ReorderableListView(IList<Widget> children, Action<int, int> onReorder)
    {
        _children = children;
        _onReorder = onReorder;
    }

    protected override Widget Build(BuildContext context)
    {
        // ReorderableList reports the final destination (already decremented); re-expand it to the
        // insertion-slot convention so handlers compute the right index.
        return new ReorderableList(
            _children,
            (from, to) => _onReorder?.Invoke(from, to > from ? to + 1 : to)
        );
    }
}

/// <summary>A selectable chip. Alias over <see cref="Chip" />.</summary>
public sealed class FilterChip : StatelessWidget
{
    private readonly Widget _label;

    public FilterChip(Widget label, bool selected = false, Action<bool>? onSelected = null)
    {
        _label = label;
        Selected = selected;
        OnSelected = onSelected;
    }

    public bool Selected { get; set; }
    public Action<bool>? OnSelected { get; set; }

    protected override Widget Build(BuildContext context)
    {
        var text = _label is Label l ? l.Text : "";
        return new Chip(text, Selected, OnSelected is null ? null : () => OnSelected(!Selected));
    }
}

/// <summary>A single-choice chip. Alias over <see cref="Chip" />.</summary>
public sealed class ChoiceChip : StatelessWidget
{
    private readonly Widget _label;

    public ChoiceChip(Widget label, bool selected = false, Action<bool>? onSelected = null)
    {
        _label = label;
        Selected = selected;
        OnSelected = onSelected;
    }

    public bool Selected { get; set; }
    public Action<bool>? OnSelected { get; set; }

    protected override Widget Build(BuildContext context)
    {
        var text = _label is Label l ? l.Text : "";
        return new Chip(text, Selected, OnSelected is null ? null : () => OnSelected(!Selected));
    }
}

/// <summary>
///     A circular container for an icon/initials/image.
///     <c>new CircleAvatar(radius: 24, child: new Text("A"))</c>. Background images are not modelled.
/// </summary>
public sealed class CircleAvatar : StatelessWidget
{
    private readonly Widget? _child;

    public CircleAvatar(Widget? child = null, Color? backgroundColor = null,
        Color? foregroundColor = null,
        double radius = 20)
    {
        _child = child;
        BackgroundColor = backgroundColor;
        ForegroundColor = foregroundColor;
        Radius = (float)radius;
    }

    public Color? BackgroundColor { get; set; }
    public Color? ForegroundColor { get; set; }
    public float Radius { get; set; }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var d = Radius * 2f;

        if (_child is Label l && l.Color is null)
            l.Color = ForegroundColor ?? theme.OnPrimary;

        return new DecoratedBox {
            Fill = BackgroundColor ?? theme.Fill3,
            Radius = Radius,
            Child = new SizedBox(d, d, new Center(_child)),
        };
    }
}