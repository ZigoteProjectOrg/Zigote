namespace Zigote.UI.Localizations;

/// <summary>
///     A <see cref="Label" /> whose text is a translation key resolved against the ambient
///     <see cref="Localizations" />. It rebuilds automatically when the locale changes (it depends on
///     the provider) and forwards the common text-styling properties to the underlying label.
///     <code>
///   new LocalizedText("greeting", ("name", user.Name)) { FontSize = 18f }
///   </code>
/// </summary>
public sealed class LocalizedText : ComposedWidget
{
    private (string Name, object? Value)[] _args;

    public LocalizedText(string key, params (string Name, object? Value)[] args)
    {
        TranslationKey = key;
        _args = args;
    }

    /// <summary>The translation key resolved against the ambient bundle.</summary>
    public string TranslationKey { get; private set; }

    public float? FontSize { get; init; }
    public Color? Color { get; init; }
    public FontWeight FontWeight { get; init; } = FontWeight.Normal;
    public FontStyle FontStyle { get; init; } = FontStyle.Normal;
    public TextAlign Align { get; init; } = TextAlign.Left;
    public int? MaxLines { get; init; }
    public TextOverflow Overflow { get; init; } = TextOverflow.Clip;
    public string? FontFamily { get; init; }

    /// <summary>Replace the message arguments and rebuild.</summary>
    public void SetArguments(params (string Name, object? Value)[] args)
    {
        _args = args;
        Invalidate();
    }

    /// <summary>Change the translation key and rebuild.</summary>
    public void SetKey(string key)
    {
        TranslationKey = key;
        Invalidate();
    }

    protected override Widget Build(BuildContext context)
    {
        string text = context.Tr(key: TranslationKey, args: _args);
        return new Label(text) {
            FontSize = FontSize,
            Color = Color,
            FontWeight = FontWeight,
            FontStyle = FontStyle,
            Align = Align,
            MaxLines = MaxLines,
            Overflow = Overflow,
            FontFamily = FontFamily,
        };
    }
}
