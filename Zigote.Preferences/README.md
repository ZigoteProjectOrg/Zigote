# Zigote.Preferences

**Declarative, reactive preferences.** Every preference is a persisted signal —
`Preference<T> : IReadableSignal<T>` — so `Computed`, `Effect`, `Watch`, and the F# `watch` work on it unchanged. Values
write through to an `IKeyValueStore` (`Zigote.Persistence`) as JSON; the backend (memory, JSON file, SQLite) is chosen
at the composition root and nothing downstream changes.

```csharp
public sealed class EditorPrefs : PreferencesProvider
{
    public Preference<bool>   ShowGrid { get; }
    public Preference<double> UiScale  { get; }

    public EditorPrefs(PreferenceStore store) : base(store, "editor")
    {
        ShowGrid = Register("showGrid", true);   // key: "editor.showGrid"
        UiScale  = Register("uiScale", 1.0);     // key: "editor.uiScale"
    }
}

var store = new PreferenceStore(new SqliteKeyValueStore(PathOf("prefs.db")));
var prefs = new EditorPrefs(store);

new Watch(() => prefs.ShowGrid.Value ? WithGrid(canvas) : canvas)   // re-renders on change
prefs.UiScale.Update(s => Math.Clamp(s + 0.1, 0.5, 3.0));           // persists + notifies
prefs.ShowGrid.Reset();                                             // one preference
prefs.Reset();                                                      // the whole group, one batch
store.ResetAll();                                                   // everything + orphan keys

// Settings UIs enumerate generically — no concrete types needed:
foreach (var provider in store.Providers)
foreach (var pref in provider.Preferences)       // IPreference: Key, ValueType, IsSet, Reset()
    BuildRow(pref);
```

- **Reads never throw:** a missing or corrupt persisted value falls back to the default with
  `IsSet == false`; a failing storage write propagates — durability failures are not silent.
- **Writes are equality-gated** and run under the reactive graph's lock: compare, storage write, and signal set are
  atomic against concurrent writers.
- **One instance per key** — the store caches; the same key with a different `T` throws.
- **NativeAOT:** use the `JsonTypeInfo<T>` overload, the same split as `SaveStore`.
- **Values should be immutable** (records, primitives, enums) — mutating a stored object in place persists nothing. Same
  rule signals already have.

**Never cache `Value` across frames and never persist by hand** — the preference *is* the source of truth; storage is an
implementation detail behind it.

Full design: `docs/preferences-and-persistence.md`.
