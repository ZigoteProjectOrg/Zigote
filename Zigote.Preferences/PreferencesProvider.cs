using System.Text.Json.Serialization.Metadata;
using Zigote.Core.State;

namespace Zigote.Preferences;

/// <summary>
///     Declarative grouping for preferences: derive, call <see cref="Register{T}(string, T, IEqualityComparer{T}?)" />
///     once per preference in the constructor, and the provider gives the group a shared key prefix,
///     a generic enumeration for settings UIs (<see cref="Preferences" />), and a group-scoped
///     <see cref="Reset" />. Constructing a provider registers it with its
///     <see cref="PreferenceStore" /> (<see cref="PreferenceStore.Providers" />), so a settings
///     window can discover every group without knowing any concrete type.
///     <para>
///         The canonical shape — properties assigned from <c>Register</c>, nothing else:
///     </para>
///     <code>
///     public sealed class EditorPreferences : PreferencesProvider
///     {
///         public Preference&lt;bool&gt;   ShowGrid { get; }
///         public Preference&lt;double&gt; UiScale  { get; }
///
///         public EditorPreferences(PreferenceStore store) : base(store, "editor")
///         {
///             ShowGrid = Register("showGrid", true);      // key: "editor.showGrid"
///             UiScale  = Register("uiScale", 1.0);        // key: "editor.uiScale"
///         }
///     }
///     </code>
/// </summary>
public abstract class PreferencesProvider
{
    private readonly List<IPreference> _registered = [];

    protected PreferencesProvider(PreferenceStore store, string? prefix = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        Store = store;
        Prefix = string.IsNullOrEmpty(prefix) ? null : prefix;
        store.RegisterProvider(this);
    }

    public PreferenceStore Store { get; }

    /// <summary>Key prefix for every registration, joined with a dot; null means bare keys.</summary>
    public string? Prefix { get; }

    /// <summary>This provider's preferences, in registration order.</summary>
    public IReadOnlyList<IPreference> Preferences
    {
        get
        {
            lock (_registered)
            {
                return _registered.ToArray();
            }
        }
    }

    /// <summary>
    ///     Every preference of this group back to its default (persisted entries removed). Other
    ///     providers on the same store are untouched. Runs as one reactive batch: an effect depending
    ///     on several of the group's preferences settles once, not once per preference.
    /// </summary>
    public void Reset()
    {
        IPreference[] snapshot;
        lock (_registered)
        {
            snapshot = _registered.ToArray();
        }

        Reactive.Sync(() => Reactive.Batch(() =>
                {
                    foreach (var preference in snapshot) preference.Reset();
                }
            )
        );
    }

    /// <summary>
    ///     Declares one preference of this group: resolves it through the store under the prefixed
    ///     key and records it for <see cref="Preferences" /> and <see cref="Reset" />.
    /// </summary>
    protected Preference<T> Register<T>(string key, T defaultValue,
        IEqualityComparer<T>? comparer = null)
    {
        return Track(Store.Preference(FullKey(key), defaultValue, comparer));
    }

    /// <summary>Reflection-free variant for NativeAOT; otherwise identical to the default overload.</summary>
    protected Preference<T> Register<T>(
        string key,
        T defaultValue,
        JsonTypeInfo<T> typeInfo,
        IEqualityComparer<T>? comparer = null)
    {
        return Track(
            Store.Preference(
                FullKey(key),
                defaultValue,
                typeInfo,
                comparer
            )
        );
    }

    private string FullKey(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return Prefix is null ? key : $"{Prefix}.{key}";
    }

    private Preference<T> Track<T>(Preference<T> preference)
    {
        lock (_registered)
        {
            if (!_registered.Contains(preference)) _registered.Add(preference);
        }

        return preference;
    }
}
