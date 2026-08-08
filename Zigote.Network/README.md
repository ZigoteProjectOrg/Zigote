# Zigote.Network

> **EXPERIMENTAL** — this library is not yet wired to the engine runtime. No host (game
> runtime, standalone player, or editor play session) creates a `NetworkManager`; only the
> test suite exercises it. To use it today, a host must construct and start a
> `NetworkManager` itself and assign it to `Net.Manager` (and clear it on stop) — until
> then every `Net` query is a safe no-op.

Client/server networking building blocks for Zigote games: transport, message serialization, replication, client
prediction, and clock synchronization. Game
`Component` scripts reach the active session through the static `Net` accessor.
