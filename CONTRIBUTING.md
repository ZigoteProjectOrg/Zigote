# Contributing

Thanks for looking at Zigote. This page is the short version of how the repository is built, tested
and changed; the long version is in [`docs/`](docs/README.md).

## Building

You need the .NET 10 SDK (`global.json` pins the version), Zig 0.17.0 on `PATH`
(`Zigote.Engine/.zigversion`), Rust via rustup (`native/zigote-svg/rust-toolchain.toml` selects the
toolchain) and `git`. `dotnet build` drives the native builds itself.

```sh
git clone --recurse-submodules https://github.com/ZigoteProjectOrg/Zigote.git
cd Zigote
dotnet build Zigote.sln
zigote doctor      # if a build complains about a toolchain (dotnet tool install -g Zigote.Cli)
```

`Zigote.Engine` is a git submodule of its own repository. A change to the native engine is a pull
request there first; the submodule pointer here moves afterwards.

## Testing

```sh
dotnet test Zigote.Tests/Zigote.Tests.csproj                       # headless, no window
dotnet run --project Zigote.UI.Adwaita.Gallery -- --self-test        # every gallery page constructs
dotnet run --project Zigote.Playground -- --self-test                # every example compiles
dotnet run --project Zigote.SmokeTest                                # boots the real renderer (GPU)
```

Widget tests are headless by design: build a tree, lay it out, dispatch synthetic input, and assert
on state and the emitted paint commands. A test project must not reference `Zigote.Editor`, which
boots the native engine. Renderer changes come with a `ZIGOTE_SHOT` golden image compared by
`tools/bmpdiff.py`; native (Zig or shader) changes are validated with `zig build shared-lib` in
`Zigote.Engine` and need a full process restart to take effect.

## Changes

- Keep the Measure → Layout → Paint path allocation-free; `HotPathAllocationTests` is the proof.
- Compose widgets from the existing primitives; hand-written `Measure` / `Layout` / `Paint` is for
  the sanctioned leaves only.
- XML-doc the public surface of every `Zigote.*` package. Elsewhere, a comment explains *why*.
- Never hand-write a native binding: the `[DllImport]` layer is generated from the Zig exports.
- Add a line under `[Unreleased]` in `CHANGELOG.md` for anything a user of the SDK would notice.

Commit messages follow the existing log: a `type(scope): summary` first line (`feat`, `fix`,
`perf`, `refactor`, `docs`, `build`, `chore`), a `!` for a breaking change, and a body that says
why. Open the pull request against `main`; CI builds the whole desktop solution, native halves
included, and runs the headless tests on Linux. The release workflow is what covers all three
desktop OSes, so a Windows- or macOS-only problem is still worth a note in the pull request.

## Reporting

Platform bug reports are especially welcome — Windows and Linux get less real-hardware time than
macOS. Include the output of `zigote doctor`, the OS and GPU, and for rendering problems a
screenshot. Security issues go to the maintainers privately rather than to the public tracker.
