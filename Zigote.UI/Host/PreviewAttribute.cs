namespace Zigote.UI.Host;

/// <summary>
///     Marks a widget class or a static factory method as a preview target, and says how it wants to be
///     shown.
///     <para>
///         Nothing has to be annotated: <see cref="WidgetPreview.Candidates" /> still finds every widget
///         a preview could construct. The attribute is for the app that has a hundred of them — an
///         annotated target is named, grouped and sorted to the top of the list, which is the difference
///         between a dropdown you pick from and one you search.
///     </para>
///     <para>
///         <see cref="Width" />/<see cref="Height" /> are <b>layout points</b>, the same units
///         <c>MediaQuery</c> and the <c>size</c> command use — a Pixel 8 is 412×915 points. A previewer
///         lays the live tree out at that size, so breakpoints behave as they would on the device rather
///         than a phone-shaped box being drawn around a desktop layout.
///     </para>
///     <example>
///         <code>
///         [Preview("Product card", Width = 412, Height = 915, Theme = "dark")]
///         public sealed class ProductCard(string title = "Espresso", bool sale = false) : Widget { … }
///         </code>
///         The constructor's defaulted parameters become the editable properties of the preview — see
///         <see cref="WidgetPreview.Descriptors" />.
///     </example>
/// </summary>
[AttributeUsage(
    validOn: AttributeTargets.Class | AttributeTargets.Method,
    Inherited = false
)]
public sealed class PreviewAttribute(string? name = null) : Attribute
{
    /// <summary>What to call it in a previewer's list; the type name when absent.</summary>
    public string? Name { get; } = name;

    /// <summary>A heading to file it under, for an app with more screens than fit in one list.</summary>
    public string? Group { get; init; }

    /// <summary>Layout width to show it at, in points. 0 leaves the size to the previewer.</summary>
    public float Width { get; init; }

    /// <summary>Layout height to show it at, in points. 0 leaves the size to the previewer.</summary>
    public float Height { get; init; }

    /// <summary><c>dark</c> or <c>light</c> — the theme this one is meant to be looked at in.</summary>
    public string? Theme { get; init; }
}
