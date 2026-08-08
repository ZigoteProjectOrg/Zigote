# Preferences & Persistence — design

**Zigote.Preferences** is a declarative, reactive preferences layer: every preference is a persisted
signal (`Preference<T> : IReadableSignal<T>`) that plugs directly into the existing reactive graph in
`Zigote.Core.State` — `Computed`, `Effect`, `Watch`, and `Ui.bind` all work on it unchanged.
**Zigote.Persistence** is the storage-agnostic base it writes through: a small, synchronous,
thread-safe key-value contract plus two built-in backends (in-memory, JSON file).
**Zigote.Persistence.SQLite** adds a SQLite backend on top of the `Microsoft.Data.Sqlite` NuGet
package. Preferences never know which backend they run on.

> **The split:** `Zigote.Persistence` answers "where do strings live" (`IKeyValueStore`);
> `Zigote.Preferences` answers "what is true now" (typed, reactive values with defaults). The
> boundary between them is a `string → string` map — nothing else crosses it, which is what makes
> the system database-agnostic.

---

## Goals and non-goals

| Goals | Non-goals (v1) |
| --- | --- |
| Reactive: reading `Value` inside a `Computed`/`Effect`/`Watch` tracks it | Cross-process change watching (file watchers, SQLite polling) |
| Declarative: a preferences class is plain properties, no ceremony | Async I/O — preference payloads are tiny; the API stays sync like `SaveStore` |
| Backend-agnostic: memory, JSON file, SQLite are interchangeable | Schema migrations — per-key defaults make most migrations unnecessary |
| Reads never throw: missing or corrupt values fall back to the default | An ORM — `Zigote.Persistence` is a key-value contract, not a data mapper |
| NativeAOT-clean path via `JsonTypeInfo<T>` overloads (same as `SaveStore`) | Encryption / secrets storage |
| Thread-safe writes from any thread (same guarantee as `Signal<T>`) | |

---

## Architecture

```text
┌─────────────────────────────────────────────────────────────┐
│ App / Editor code                                           │
│   sealed class EditorPrefs { Preference<bool> ShowGrid;… }  │
└──────────────────────────┬──────────────────────────────────┘
                           │ typed, reactive
┌──────────────────────────▼──────────────────────────────────┐
│ Zigote.Preferences                (refs: Core, Persistence) │
│   PreferenceStore  — key → Preference<T> cache, JSON codec  │
│   Preference<T>    — Signal<T> + write-through persistence  │
└──────────────────────────┬──────────────────────────────────┘
                           │ string key → string value (JSON)
┌──────────────────────────▼──────────────────────────────────┐
│ Zigote.Persistence                          (refs: BCL only)│
│   IKeyValueStore                                            │
│   InMemoryKeyValueStore   JsonFileKeyValueStore             │
└──────────────────────────┬──────────────────────────────────┘
                           │ implemented by
┌──────────────────────────▼──────────────────────────────────┐
│ Zigote.Persistence.SQLite   (refs: Persistence,             │
│   SqliteKeyValueStore        Microsoft.Data.Sqlite)         │
└─────────────────────────────────────────────────────────────┘
```

Dependency direction follows the repo rule: `Zigote.Persistence` depends on the BCL only;
`Zigote.Preferences` depends on `Zigote.Core` (signals) and `Zigote.Persistence`; the SQLite package
is a leaf that nothing in the solution is forced to reference.

---

## Zigote.Persistence

### `IKeyValueStore`

```csharp
namespace Zigote.Persistence;

public interface IKeyValueStore : IDisposable
{
    bool TryGet(string key, out string value);
    void Set(string key, string value);
    bool Remove(string key);
    bool Contains(string key);
    IReadOnlyList<string> Keys();
    void Clear();
    void Flush();
}
```

Contract rules every implementation must honor:

- **Thread-safe.** All members callable from any thread; implementations lock internally.
  `Preference<T>` writes from whatever thread mutated the signal.
- **Keys are opaque non-empty strings.** Dot-separated namespacing (`"editor.showGrid"`) is a
  convention, not a rule. Implementations must not interpret keys (no path mapping — one file/table
  per store, so keys can never escape onto the filesystem).
- **Values are opaque strings.** The preferences layer stores JSON, but the store must round-trip
  any string verbatim.
- **`TryGet` never throws** for a missing key; it returns `false`. `Set` may throw (disk full,
  locked database) — durability failures must not be silent.
- **`Flush` is a durability barrier.** Backends that buffer (the JSON file store in manual mode)
  persist on `Flush`; write-through backends (SQLite, eager file mode) treat it as a no-op.
  `Dispose` implies `Flush`.

### Built-in backends

| Backend | File(s) | Semantics |
| --- | --- | --- |
| `InMemoryKeyValueStore` | none | Dictionary + lock. Tests, ephemeral runs, previews. |
| `JsonFileKeyValueStore` | one JSON object file | Ordinal-sorted keys, indented — diff-friendly. Atomic write: `.tmp` then `File.Move(overwrite: true)`, same as `SaveStore`. `autoFlush: true` (default) persists on every mutation; `false` buffers until `Flush`/`Dispose`. A corrupt file is copied aside to `<path>.corrupt` and the store starts empty — data is quarantined, never silently destroyed, and loading never throws. |

## Zigote.Persistence.SQLite

`SqliteKeyValueStore` builds on **`Microsoft.Data.Sqlite`** (the maintained, AOT-friendly ADO.NET
provider that bundles the native `e_sqlite3` library — no hand-rolled P/Invoke, per the repo's FFI
rules).

```csharp
public sealed class SqliteKeyValueStore : IKeyValueStore
{
    public SqliteKeyValueStore(string path, string tableName = "preferences");
}
```

- Schema: `CREATE TABLE IF NOT EXISTS <table> (key TEXT PRIMARY KEY, value TEXT NOT NULL)`.
- Writes are upserts (`INSERT … ON CONFLICT(key) DO UPDATE`), durable immediately; `Flush` is a
  no-op. `PRAGMA journal_mode=WAL` keeps concurrent readers cheap.
- One connection per store, pooling disabled, guarded by a lock — disposal releases the file
  deterministically.
- `tableName` must match `[A-Za-z_][A-Za-z0-9_]*`; anything else throws `ArgumentException`
  (identifiers cannot be parameterized, so the name is validated instead).
- Multiple stores may share one database file with different table names.

---

## Zigote.Preferences

### `Preference<T>` — a persisted signal

```csharp
namespace Zigote.Preferences;

public interface IPreference : ISignal      // the non-generic face, for settings UIs and group resets
{
    string Key { get; }
    bool IsSet { get; }
    Type ValueType { get; }
    void Reset();
}

public sealed class Preference<T> : IReadableSignal<T>, IPreference
{
    public string Key { get; }
    public T Default { get; }
    public bool IsSet { get; }              // true when a persisted value exists
    public T Value { get; set; }            // get: tracked read; set: equality-gated write-through
    public T Peek();                        // read without subscribing
    public void Update(Func<T, T> update);  // atomic read-modify-write
    public void Reset();                    // back to Default, removes the persisted entry
    public IDisposable Subscribe(Action<T> listener);   // fires immediately, then on change
    public event Action? Invalidated;
    public event Action<T>? Changed;
}
```

Semantics:

- **It is a `Signal<T>` that writes through.** Internally each preference owns a
  `Signal<T>(loadedOrDefault, comparer)`. Reads delegate to the signal, so dependency tracking,
  `Reactive.Batch`, `Untracked`, and `Watch` behave exactly as for any signal.
- **Writes are equality-gated** by the same comparer the signal uses: setting an unchanged value
  neither notifies nor touches storage. The first explicit set always persists, even when the value
  equals the default — the user chose it.
- **Write path runs under `Reactive.Sync`** (the graph's re-entrant lock), so compare + signal set +
  storage write are atomic against concurrent writers and cannot deadlock against effect drains.
- **Loads never throw; stores may.** A missing or unparseable persisted value yields `Default` with
  `IsSet == false` (the corrupt entry is left in place for inspection). A failing storage write
  propagates to the setter's caller.
- **Values should be immutable** (records, primitives, enums) — the same rule signals already have.
  Mutating a stored object in place persists nothing.

### `PreferenceStore`

```csharp
public sealed class PreferenceStore : IDisposable   // owns and disposes the storage
{
    public PreferenceStore(IKeyValueStore storage, JsonSerializerOptions? options = null);

    public Preference<T> Preference<T>(string key, T defaultValue,
                                       IEqualityComparer<T>? comparer = null);
    public Preference<T> Preference<T>(string key, T defaultValue, JsonTypeInfo<T> typeInfo,
                                       IEqualityComparer<T>? comparer = null);  // NativeAOT path

    public IReadOnlyList<PreferencesProvider> Providers { get; }  // construction order

    public void ResetAll();   // every known preference back to Default; storage cleared; one batch
    public void Flush();      // durability barrier, forwards to storage
}
```

- **One instance per key.** The store caches preferences by key; asking twice returns the same
  object (the first call's default and comparer win). Asking for the same key with a different `T`
  throws `InvalidOperationException` — two live signals over one entry would race.
- **Values are serialized as JSON** through `System.Text.Json`. The reflection overload is fine
  under JIT; the `JsonTypeInfo<T>` overload mirrors `SaveStore` for NativeAOT.
- The store **owns the backend**: `Dispose` flushes and disposes the `IKeyValueStore`.

### `PreferencesProvider` — declarative registration and group reset

`PreferencesProvider` is the declarative grouping layer: derive, call `Register` once per
preference in the constructor, and the group gets a shared key prefix, generic enumeration, and a
scoped reset. Constructing a provider registers it with its store (`store.Providers`), so a
settings window discovers every group without knowing any concrete type.

```csharp
public abstract class PreferencesProvider
{
    protected PreferencesProvider(PreferenceStore store, string? prefix = null);

    public PreferenceStore Store { get; }
    public string? Prefix { get; }                       // joined to keys with a dot
    public IReadOnlyList<IPreference> Preferences { get; }   // registration order

    public void Reset();   // this group only, coalesced into one reactive batch

    protected Preference<T> Register<T>(string key, T defaultValue,
                                        IEqualityComparer<T>? comparer = null);
    protected Preference<T> Register<T>(string key, T defaultValue, JsonTypeInfo<T> typeInfo,
                                        IEqualityComparer<T>? comparer = null);
}
```

The reset feature is a three-level hierarchy; every level removes the persisted entries so the next
load is unset, and the group/store levels run as one `Reactive.Batch`, so an effect depending on
several affected preferences settles once, not once per preference:

| Scope | Call | Effect |
| --- | --- | --- |
| One preference | `pref.Reset()` (also on `IPreference`) | Back to `Default`, entry removed |
| One group | `provider.Reset()` | Every registered preference of that provider; others untouched |
| Everything | `store.ResetAll()` | Every materialized preference + storage cleared, including orphan keys never materialized this run |

### Declarative usage — the canonical pattern

```csharp
public sealed class EditorPrefs : PreferencesProvider
{
    public Preference<bool>      ShowGrid { get; }
    public Preference<double>    UiScale  { get; }
    public Preference<ThemeMode> Theme    { get; }

    public EditorPrefs(PreferenceStore store) : base(store, "editor")
    {
        ShowGrid = Register("showGrid", true);       // key: "editor.showGrid"
        UiScale  = Register("uiScale", 1.0);         // key: "editor.uiScale"
        Theme    = Register("theme", ThemeMode.Dark);
    }
}

// Composition root — pick a backend, nothing downstream changes:
var store = new PreferenceStore(new SqliteKeyValueStore(PathOf("prefs.db")));
// var store = new PreferenceStore(new JsonFileKeyValueStore(PathOf("prefs.json")));
// var store = new PreferenceStore(new InMemoryKeyValueStore());          // tests
var prefs = new EditorPrefs(store);

// Reactive consumption — a Watch re-renders when the preference changes:
new Watch(() => prefs.ShowGrid.Value ? WithGrid(canvas) : canvas)

// Derived state:
var effectiveScale = Computed.From(() => prefs.UiScale.Value * dpi.Value);

// Writes persist immediately and notify every subscriber:
prefs.ShowGrid.Value = false;
prefs.UiScale.Update(s => Math.Clamp(s + 0.1, 0.5, 3.0));
prefs.Theme.Reset();          // one preference
prefs.Reset();                // the whole "editor" group, one batch

// A generic settings window needs no concrete types at all:
foreach (var provider in store.Providers)
foreach (var pref in provider.Preferences)
    BuildRow(pref.Key, pref.ValueType, pref.IsSet, resetRow: pref.Reset);
```

**Never cache `Value` across frames and never persist by hand** — the preference *is* the source of
truth; storage is an implementation detail behind it.

---

## Storage format

One JSON-encoded value per key. For `JsonFileKeyValueStore` the whole store is a single object:

```text
{
    "editor.showGrid": "false",
    "editor.theme": "\"Dark\"",
    "editor.uiScale": "1.25"
}
```

Values are JSON *strings containing JSON* deliberately: the file store needs no type knowledge to
round-trip unknown keys, and every backend (TEXT column, dictionary, file) stores the identical
payload. `SaveStore`'s envelope/versioning is not reused — preferences are per-key values with
per-key defaults, not a monolithic versioned document.

## Testing

All tests live in `Zigote.Tests`, xunit, temp directories via `Directory.CreateTempSubdirectory`
(the `SaveStoreTests` pattern). Backend tests run once per implementation against the shared
contract (round-trip, overwrite, remove, keys, clear, persistence across instances, corrupt-file
quarantine, table-name validation). Preference tests join `[Collection("Reactive-serial")]` since
the reactive graph has process-static state, and cover: default fallback, write-through,
reload-from-storage, equality gating, `Computed`/`Subscribe` reactivity, `Reset`, type-mismatch
rejection, corrupt-value fallback, and the `JsonTypeInfo` path.

## Notes

- **Future work:** an `ExternalChanged` event on `IKeyValueStore` for multi-window sync; a debounced
  write policy if a preference ever ends up on a hot path; optional store-level versioning if a
  breaking key rename is ever needed. All are additive.
- The editor runs on this system: `EditorSettings` and `ProjectHistory`
  (`Zigote.Editor/Settings/`) are two `PreferencesProvider` groups over one SQLite store at
  `<AppData>/Zigote/preferences.db` — settings in `editor.*`, recent/last project in
  `projects.*`, so the Settings window's "Reset All" (the editor group) leaves history alone.
  `EditorPreferences` is the reactive applier layer (effects observing the preferences). The old
  `EditorConfig`/editor.json layer is gone.
- Project-level editor preferences use the same model with a different backend:
  `ProjectPreferences` owns a `JsonFileKeyValueStore` at the project-relative
  `<project>.prefs.json`, with `ViewportPreferences` (`viewport.*` — debug-viz toggles, snap grid)
  and `LayoutPreferences` (`layout.*` — the dock tree as one `DockLayoutData` value, replacing the
  standalone `<project>.layout.json`). The debug-console `render.*` variables and toolbar write
  the preferences; session bindings mirror them into the `EditorState` flags the viewport reads
  per paint.
- See `Zigote.Core/README.md` for the signal/event distinction and `Zigote.Save/SaveStore.cs` for
  the never-throw-on-read precedent both layers follow.
