namespace Zigote.UI.Material;

/// <summary>
///     One option in a <see cref="DropdownButton{T}" />,
///     pairing a <see cref="Value" /> with a display child. Only the child's text is rendered.
/// </summary>
public sealed class DropdownMenuItem<T>
{
    public DropdownMenuItem(T value, Widget child)
    {
        Value = value;
        Child = child;
        Label = child is Label l ? l.Text : "";
    }

    public DropdownMenuItem(T value, string child)
    {
        Value = value;
        Child = new Label(child);
        Label = child;
    }

    public T Value { get; }
    public Widget Child { get; }
    public string Label { get; }
}

/// <summary>
///     A value-based dropdown. Alias over
///     <see cref="Dropdown{T}" /> that maps <c>value</c>/<c>onChanged</c> (by value) onto the
///     index-based control.
///     <c>
///         new DropdownButton&lt;string&gt;(value: sel, items: [ new
///         DropdownMenuItem&lt;string&gt;("a", new Text("A")) ], onChanged: (v) => …)
///     </c>
///     .
/// </summary>
public sealed class DropdownButton<T> : Dropdown<T>
{
    public DropdownButton(
        List<DropdownMenuItem<T>> items,
        T? value = default,
        Action<T?>? onChanged = null,
        Widget? hint = null)
        : base(
            items: items.ConvertAll(i => i.Value),
            selectedIndex: IndexOf(items: items, value: value),
            displayText: v => LabelFor(items: items, v: v),
            onChanged: (_, val) => onChanged?.Invoke(val)
        ) =>
        _ = hint;

    private static int IndexOf(List<DropdownMenuItem<T>> items, T? value)
    {
        if (value is null) return 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(x: items[i].Value, y: value))
                return i;
        }

        return 0;
    }

    private static string LabelFor(List<DropdownMenuItem<T>> items, T v)
    {
        foreach (var it in items)
        {
            if (EqualityComparer<T>.Default.Equals(x: it.Value, y: v))
                return it.Label;
        }

        return v?.ToString() ?? "";
    }
}
