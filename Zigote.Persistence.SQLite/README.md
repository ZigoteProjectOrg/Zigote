# Zigote.Persistence.SQLite

**SQLite backend for `Zigote.Persistence`**, built on the `Microsoft.Data.Sqlite` NuGet package (bundled native
`e_sqlite3` — no hand-rolled P/Invoke, per the repo's FFI rules). A leaf project:
nothing in the solution is forced to reference it.

```csharp
using var store = new SqliteKeyValueStore("prefs.db");            // table "preferences"
using var other = new SqliteKeyValueStore("prefs.db", "layout");  // same file, second table
```

- One `(key TEXT PRIMARY KEY, value TEXT NOT NULL)` table per store; multiple stores can share a database file via
  distinct table names.
- Writes are upserts, durable immediately — `Flush` is a no-op. `journal_mode=WAL` keeps concurrent readers cheap.
- One connection per store, pooling disabled, lock-guarded: disposal releases the file deterministically.
- Table names must match `[A-Za-z_][A-Za-z0-9_]*`; anything else throws (identifiers cannot be parameterized, so the
  name is validated instead).

Full design: `docs/preferences-and-persistence.md`.
