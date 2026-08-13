using System.Collections;
using System.Collections.Concurrent;

namespace Zigote.UI.Localizations;

/// <summary>
///     A single locale's message table — key → ICU message template — with per-key compiled
///     <see cref="MessageFormat" /> caching. Authored declaratively:
///     <code>
///   var en = new LocalizationCatalog(Locale.En)
///   {
///       ["app.title"] = "My App",
///       ["greeting"]  = "Hello, {name}!",
///       ["items"]     = "{count, plural, =0 {No items} one {# item} other {# items}}",
///   };
///   </code>
///     A malformed template never throws at format time — it falls back to its raw text.
/// </summary>
public sealed class LocalizationCatalog : IEnumerable<KeyValuePair<string, string>>
{
    private static readonly IReadOnlyDictionary<string, object?> NoArgs =
        new Dictionary<string, object?>(0);

    private readonly ConcurrentDictionary<string, MessageFormat> _compiled =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _messages;

    public LocalizationCatalog(Locale locale)
    {
        Locale = locale;
        _messages = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public LocalizationCatalog(Locale locale, IEnumerable<KeyValuePair<string, string>> messages)
        : this(locale) =>
        AddRange(messages);

    public Locale Locale { get; }

    public int Count => _messages.Count;
    public IReadOnlyCollection<string> Keys => _messages.Keys;

    /// <summary>The raw template for a key. The setter replaces it and drops any compiled form.</summary>
    public string this[string key]
    {
        get => _messages[key];
        set => Add(key: key, template: value);
    }

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _messages.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Add(string key, string template)
    {
        _messages[key] = template ?? throw new ArgumentNullException(nameof(template));
        _compiled.TryRemove(key: key, value: out _);
    }

    public void AddRange(IEnumerable<KeyValuePair<string, string>> messages)
    {
        foreach (var kv in messages) Add(key: kv.Key, template: kv.Value);
    }

    public bool Contains(string key) => _messages.ContainsKey(key);

    public bool TryGetTemplate(string key, out string template) =>
        _messages.TryGetValue(key: key, value: out template!);

    /// <summary>Translate a key, or return <c>null</c> when the key is absent from this catalog.</summary>
    public string? Translate(string key, IReadOnlyDictionary<string, object?>? args = null)
    {
        if (!_messages.TryGetValue(key: key, value: out string? template)) return null;
        return Render(key: key, template: template, args: args ?? NoArgs);
    }

    public string? Translate(string key, params (string Name, object? Value)[] args) => Translate(
        key: key,
        args: MessageFormat.ToDictionary(args)
    );

    private string Render(string key, string template, IReadOnlyDictionary<string, object?> args)
    {
        if (!_compiled.TryGetValue(key: key, value: out var format))
        {
            try
            {
                format = new MessageFormat(template);
            }
            catch (FormatException)
            {
                return template; // unparseable template — show its raw text rather than crash
            }

            _compiled[key] = format;
        }

        try
        {
            return format.Format(locale: Locale, arguments: args);
        }
        catch (FormatException)
        {
            return template;
        }
    }
}
