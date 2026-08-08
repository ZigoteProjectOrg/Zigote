# Zigote.Persistence

**Storage-agnostic key-value persistence.** A small, synchronous, thread-safe `string → string`
contract (`IKeyValueStore`) plus two built-in backends. Depends on the BCL only — no engine, no UI, no reactive graph.
`Zigote.Preferences` sits on top; `Zigote.Persistence.SQLite` plugs in below.

> **The split:** this layer answers "where do strings live". What the strings *mean* (types,
> defaults, reactivity) is `Zigote.Preferences`' job — nothing but opaque keys and values crosses
> the boundary, which is what keeps the system database-agnostic.

| Type                    | Role                                                                                                                                               |
|-------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------|
| `IKeyValueStore`        | The contract: `TryGet` / `Set` / `Remove` / `Contains` / `Keys` / `Clear` / `Flush`                                                                |
| `InMemoryKeyValueStore` | Ephemeral; tests and runs that must not touch the disk                                                                                             |
| `JsonFileKeyValueStore` | One sorted, indented JSON object per store; atomic `.tmp` + rename writes; corrupt files quarantined to `<path>.corrupt`, never silently destroyed |

**Rules for implementations:** all members thread-safe; keys and values are opaque and round-trip verbatim; `TryGet`
never throws for a missing key; `Set` may throw — durability failures must not be silent; `Flush` is a durability
barrier and disposal implies it.

Full design: `docs/preferences-and-persistence.md`.
