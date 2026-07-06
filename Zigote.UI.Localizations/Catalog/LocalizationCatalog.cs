using System.Collections;
using System.Collections.Concurrent;
using Zigote.UI.Host;

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
        : this(locale)
    {
        AddRange(messages);
    }

    public Locale Locale { get; }

    public int Count => _messages.Count;
    public IReadOnlyCollection<string> Keys => _messages.Keys;

    /// <summary>The raw template for a key. The setter replaces it and drops any compiled form.</summary>
    public string this[string key]
    {
        get => _messages[key];
        set => Add(key, value);
    }

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
    {
        return _messages.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public void Add(string key, string template)
    {
        _messages[key] = template ?? throw new ArgumentNullException(nameof(template));
        _compiled.TryRemove(key, out _);
    }

    public void AddRange(IEnumerable<KeyValuePair<string, string>> messages)
    {
        foreach (var kv in messages) Add(kv.Key, kv.Value);
    }

    public bool Contains(string key)
    {
        return _messages.ContainsKey(key);
    }

    public bool TryGetTemplate(string key, out string template)
    {
        return _messages.TryGetValue(key, out template!);
    }

    /// <summary>Translate a key, or return <c>null</c> when the key is absent from this catalog.</summary>
    public string? Translate(string key, IReadOnlyDictionary<string, object?>? args = null)
    {
        if (!_messages.TryGetValue(key, out var template)) return null;
        return Render(key, template, args ?? NoArgs);
    }

    public string? Translate(string key, params (string Name, object? Value)[] args)
    {
        return Translate(key, MessageFormat.ToDictionary(args));
    }

    private string Render(string key, string template, IReadOnlyDictionary<string, object?> args)
    {
        if (!_compiled.TryGetValue(key, out var format))
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
            return format.Format(Locale, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }
}