using LayoutPadding = Zigote.UI.Widgets.Layout.Padding;

namespace Zigote.UI.Material;

/// <summary>
///     A single fixed-height row: optional <see cref="Leading" />, a
///     <see cref="Title" /> over an optional <see cref="Subtitle" />, and an optional
///     <see cref="Trailing" />. Tappable via <see cref="OnPressed" />; highlights when
///     <see cref="Selected" />.
///     <c>
///         new ListTile(leading: new Icon("wifi"), title: new Text("Wi-Fi"),
///         trailing: new Switch(on, …), onPressed: () => …)
///     </c>
///     .
/// </summary>
public sealed class ListTile : ComposedWidget
{
    public ListTile(
        Widget? leading = null,
        Widget? title = null,
        Widget? subtitle = null,
        Widget? trailing = null,
        Action? onPressed = null,
        bool selected = false)
    {
        Leading = leading;
        Title = title;
        Subtitle = subtitle;
        Trailing = trailing;
        OnPressed = onPressed;
        Selected = selected;
    }

    public Widget? Leading { get; set; }
    public Widget? Title { get; set; }
    public Widget? Subtitle { get; set; }
    public Widget? Trailing { get; set; }
    public Action? OnPressed { get; set; }
    public bool Selected { get; set; }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);

        var center = new Column(
            crossAxisAlignment: CrossAxisAlignment.Start,
            mainAxisSize: MainAxisSize.Min
        );
        if (Title is not null) center.Children.Add(Title);
        if (Subtitle is not null) center.Children.Add(Subtitle);

        var main = new Row(crossAxisAlignment: CrossAxisAlignment.Center);
        if (Leading is not null)
        {
            main.Children.Add(Leading);
            main.Children.Add(new SizedBox(Spacing.Md));
        }

        main.Children.Add(new Expanded(center));

        // The tap area (Pressable) covers the leading + text only. Keeping an interactive Trailing
        // (Switch/Checkbox/Radio) OUTSIDE the Pressable lets it receive its own pointer events —
        // otherwise the Pressable captures the whole row and the trailing control can't be toggled.
        //
        // 8pt of vertical padding leaves a caption-only tile ~30pt tall; a phone list row wants the
        // Material 48pt metric, which Md padding around a 13pt label reaches.
        var padV = TouchMetrics.IsCompact ? Spacing.Md : Spacing.Sm;
        Widget tap = new Pressable {
            Child = new LayoutPadding(EdgeInsets.Symmetric(Spacing.Md, padV), main),
            OnPressed = () => OnPressed?.Invoke(),
            Enabled = OnPressed is not null,
            SelectedState = Selected,
        };

        Widget content;
        if (Trailing is not null)
            content = new Row(
                crossAxisAlignment: CrossAxisAlignment.Center,
                children: [
                    new Expanded(tap),
                    Trailing,
                    new SizedBox(Spacing.Md),
                ]
            );
        else
            content = tap;

        if (Selected) content = new ColoredBox(theme.Selection, content);

        return content;
    }
}

/// <summary>A <see cref="ListTile" /> with a trailing <see cref="Switch" />.</summary>
public sealed class SwitchListTile : ComposedWidget
{
    public SwitchListTile(
        bool value,
        Action<bool>? onChanged = null,
        Widget? title = null,
        Widget? subtitle = null,
        Widget? secondary = null)
    {
        Value = value;
        OnChanged = onChanged;
        Title = title;
        Subtitle = subtitle;
        Secondary = secondary;
    }

    public bool Value { get; set; }
    public Action<bool>? OnChanged { get; set; }
    public Widget? Title { get; set; }
    public Widget? Subtitle { get; set; }
    public Widget? Secondary { get; set; }

    protected override Widget Build(BuildContext context)
    {
        return new ListTile(
            Secondary,
            Title,
            Subtitle,
            new Switch(Value, OnChanged),
            OnChanged is null ? null : () => OnChanged(!Value)
        );
    }
}

/// <summary>A <see cref="ListTile" /> with a trailing <see cref="Checkbox" />.</summary>
public sealed class CheckboxListTile : ComposedWidget
{
    public CheckboxListTile(
        bool value,
        Action<bool>? onChanged = null,
        Widget? title = null,
        Widget? subtitle = null,
        Widget? secondary = null)
    {
        Value = value;
        OnChanged = onChanged;
        Title = title;
        Subtitle = subtitle;
        Secondary = secondary;
    }

    public bool Value { get; set; }
    public Action<bool>? OnChanged { get; set; }
    public Widget? Title { get; set; }
    public Widget? Subtitle { get; set; }
    public Widget? Secondary { get; set; }

    protected override Widget Build(BuildContext context)
    {
        return new ListTile(
            Secondary,
            Title,
            Subtitle,
            new Checkbox(Value, OnChanged),
            OnChanged is null ? null : () => OnChanged(!Value)
        );
    }
}

/// <summary>A <see cref="ListTile" /> with a trailing <see cref="Radio{T}" />.</summary>
public sealed class RadioListTile<T> : ComposedWidget where T : IEquatable<T>
{
    public RadioListTile(
        T value,
        T groupValue,
        Action<T>? onChanged = null,
        Widget? title = null,
        Widget? subtitle = null,
        Widget? secondary = null)
    {
        Value = value;
        GroupValue = groupValue;
        OnChanged = onChanged;
        Title = title;
        Subtitle = subtitle;
        Secondary = secondary;
    }

    public T Value { get; set; }
    public T GroupValue { get; set; }
    public Action<T>? OnChanged { get; set; }
    public Widget? Title { get; set; }
    public Widget? Subtitle { get; set; }
    public Widget? Secondary { get; set; }

    protected override Widget Build(BuildContext context)
    {
        return new ListTile(
            Secondary,
            Title,
            Subtitle,
            new Radio<T>(Value, GroupValue, OnChanged),
            OnChanged is null ? null : () => OnChanged(Value)
        );
    }
}
