# Releasing

A release is two tags in two repositories, in a fixed order, and the version string is the same in
both. Everything a consumer can install — the NuGet packages, the `zigote` tool, the editor bundles,
the prebuilt engine — is produced by CI from those tags; nothing is built on a laptop and uploaded
by hand except the mobile engine packages (see [Mobile engine packages](#mobile-engine-packages)).

The reason for the order: the managed packages and `Zigote.Engine.runtime.<rid>` of one version are
two halves of one ABI, and the SDK release pins the engine by commit through the `Zigote.Engine`
submodule. The engine therefore has to be tagged first, and the SDK's tag has to point at a commit
whose submodule is that engine tag.

## 1. Release the engine

In [Zigote.Engine](https://github.com/ZigoteProjectOrg/Zigote.Engine), on `main`:

1. Set the version in **two** places: `.version` in `build.zig.zon`, and the shared library's
   `.version` in `build.zig` (the `addLibrary` call).
2. Move the `[Unreleased]` entries in `CHANGELOG.md` under `## [X.Y.Z] - YYYY-MM-DD`, state the
   `abi_version` (`src/ffi/root.zig`) the release carries, and update the compare links at the
   bottom.
3. Commit, tag and push:

   ```sh
   git commit -am "release: X.Y.Z"
   git tag vX.Y.Z
   git push origin main vX.Y.Z
   ```

The engine's [`release.yml`](https://github.com/ZigoteProjectOrg/Zigote.Engine/blob/main/.github/workflows/release.yml)
refuses the tag if its version disagrees with `build.zig.zon` or the changelog has no section for
it, builds `libzigote` for the five desktop RIDs, and attaches the archives plus `SHA256SUMS` to
the tag's release with the changelog section as the notes. Wait for it to go green before moving
on — the SDK release pins this commit.

## 2. Release the SDK

In this repository, on `main`:

1. Pin the submodule to the engine tag:

   ```sh
   git -C Zigote.Engine fetch --tags origin
   git -C Zigote.Engine checkout vX.Y.Z
   git add Zigote.Engine
   ```

2. Set `build/version.txt` to `X.Y.Z`. That one file is `$(ZigoteVersion)` for every package, the
   CLI, and the version `zigote create` writes into a generated project.
3. Move the `[Unreleased]` entries in `CHANGELOG.md` under `## [X.Y.Z] - YYYY-MM-DD`, name the
   engine version and ABI it pairs with, and update the compare links.
4. Build and test once, headless, to be sure the pinned engine and the managed side agree:

   ```sh
   dotnet build Zigote.sln
   dotnet test Zigote.Tests/Zigote.Tests.csproj
   ```

5. Commit, tag and push:

   ```sh
   git commit -am "release: X.Y.Z"
   git tag vX.Y.Z
   git push origin main vX.Y.Z
   ```

[`.github/workflows/release.yml`](.github/workflows/release.yml) then:

| Job | Runs on | Produces |
|---|---|---|
| `verify` | ubuntu | Nothing — fails the run early if the tag, `build/version.txt`, the changelog or the pinned engine's `build.zig.zon` disagree. |
| `publish` | macOS, ubuntu, windows | `zigote-editor-<rid>.zip`, a self-contained editor bundle per desktop RID. |
| `pack-managed` | ubuntu | Every managed package (`Zigote.UI`, `Zigote.UI.Adwaita`, …, `Zigote.Cli`) plus `Zigote.Android.Support` and `Zigote.iOS.Support`, with symbols. |
| `pack-engine` | macOS, ubuntu, windows | `Zigote.Engine.runtime.osx-arm64`, `.linux-x64`, `.win-x64` — each built natively on its own OS, which is the only way to ship the MSVC ABI on Windows. |
| `release` | ubuntu | The GitHub release for the tag: every zip and `.nupkg`, `SHA256SUMS`, and the changelog section as the notes. |
| `nuget` | ubuntu | Signs and pushes the packages to nuget.org — only when the `ZIGOTE_PUBLISH_NUGET` variable is `true` and the secrets below are set, and never unsigned. |

A tag of the form `vX.Y.Z-rc.1` is a prerelease: `build/version.txt` stays at `X.Y.Z`, the suffix
is passed through as `$(ZigoteVersionSuffix)`, and the GitHub release is marked as a prerelease.

### Publishing to NuGet

Off by default. The `nuget` job runs only when the repository **variable** `ZIGOTE_PUBLISH_NUGET`
is set to `true` (Settings → Secrets and variables → Actions → Variables); until then a release is
the GitHub release, with the `.nupkg` files attached, and an app consumes it through a local feed.
Turning it on needs these secrets:

| Secret | Used for |
|---|---|
| `NUGET_API_KEY` | Pushing to nuget.org. Without it the `nuget` job logs that it skipped and succeeds; the packages are still on the GitHub release. |
| `ZIGOTE_SIGN_CERT_B64` | The author-signing certificate, a PKCS#12 file as base64 (`base64 -w0 zigote-release.pfx`). |
| `ZIGOTE_SIGN_CERT_PASSWORD` | Its password. |

Pushing refuses to run without the certificate — the same rule as `build/pack.sh --push`. A
published SDK that cannot be verified after the fact is one nobody can tell from a tampered copy.

### Mobile engine packages

`Zigote.Engine.runtime.android-arm64`, `.android-x64` and the iOS packages are not built by CI yet:
they need an NDK or Xcode and the matching Rust targets on the runner. Until they are, pack them on
a machine that has those, at the tagged commit, and attach and push them by hand:

```sh
git checkout vX.Y.Z && git submodule update
build/pack.sh --sign android-arm64 android-x64 iossimulator-arm64 ios-arm64
gh release upload vX.Y.Z artifacts/packages/Zigote.Engine.runtime.*.nupkg --clobber
build/pack.sh --sign --push nuget.org android-arm64 android-x64 iossimulator-arm64 ios-arm64
```

The same version, the same commit and the same certificate, so `zigote doctor` sees one SDK.

## 3. Check it from the outside

```sh
dotnet tool install -g Zigote.Cli --version X.Y.Z
zigote create Smoke && cd Smoke && zigote run
```

If the packages went to the GitHub release but not yet to NuGet, point a local feed at the
downloaded `.nupkg` files first (`dotnet nuget add source <dir> -n zigote-local`) and verify them
with `sha256sum -c SHA256SUMS` and `dotnet nuget verify`.

## Re-running a release

Both workflows create the release and then upload with `--clobber`, so re-running a failed leg on
the same tag replaces its assets instead of failing on "release already exists". A tag is never
moved: a release with a wrong artifact gets the fix in `X.Y.Z+1`.
