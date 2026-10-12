#!/usr/bin/env bash
#
# Run a command inside the Linux build image (build/Dockerfile.linux: Debian 12, pinned zig + rust).
#
#   build/container.sh                                   # development build: Debug, Zigote.slnf
#   build/container.sh dotnet run --project Zigote.Tests -- -class TextTests
#   build/container.sh bash                              # a shell with the pinned toolchains
#   build/container.sh [-v host:ctr]... [-e K=V]... cmd  # extra engine mounts/env (publish.sh uses this)
#   build/container.sh --or-host cmd                     # run cmd on the host if there is no engine
#
# The repo, the NuGet cache and the tool caches are mounted at their host paths and the command runs
# as the invoking user in the caller's working directory, so bin/, obj/ and zig-out/ are the same
# files a host build produces and stay usable from both sides. Build here, run on the host: the image
# has no GPU/display stack, and anything it links has an OLDER glibc floor than the host, never newer.
#
# Three callers: developers (above), publish.sh --container, and MSBuild — $(ZigoteContainerExec) in
# Directory.Build.props routes the zig/cargo steps of a Rider build through this script.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

OR_HOST=0
[ "${1:-}" = --or-host ] && { OR_HOST=1; shift; }
EXTRA=()
while [ "${1:-}" = -v ] || [ "${1:-}" = -e ]; do EXTRA+=("$1" "$2"); shift 2; done
[ $# -eq 0 ] && set -- dotnet build "$ROOT/Zigote.slnf" -c Debug

# Already inside the image (a whole-build re-entry reaching an MSBuild step): nothing to wrap.
if [ -e /run/.containerenv ] || [ -e /.dockerenv ]; then exec "$@"; fi

ENGINE="$(command -v podman || command -v docker)" || {
  [ "$OR_HOST" -eq 1 ] && exec "$@"
  echo "build/container.sh needs podman or docker on PATH" >&2; exit 2
}
IMAGE="${ZIGOTE_BUILD_IMAGE:-zigote-build-linux}"

# Build once; rebuild only when the Dockerfile changes (the layer cache makes a no-op rebuild cheap,
# and skipping it entirely would silently keep a stale toolchain).
"$ENGINE" image inspect "$IMAGE" >/dev/null 2>&1 ||
  echo "Building $IMAGE from build/Dockerfile.linux (first run only, takes a few minutes)..." >&2
# The zig version comes from the engine's own pin, so the image cannot drift from it again.
"$ENGINE" build -q -t "$IMAGE" --build-arg "ZIG_VERSION=$(cat "$ROOT/Zigote.Engine/.zigversion")" -f "$ROOT/build/Dockerfile.linux" "$ROOT/build" >/dev/null ||
  { echo "failed to build $IMAGE" >&2; exit 1; }

# Mounted at its real path so ZigoteRoot, relative ProjectReferences and the artifacts directory all
# resolve to the same strings they would on the host.
MOUNTS=(-v "$ROOT:$ROOT")
# The package cache is mounted at the SAME absolute path it has on the host, not at the container's
# HOME. obj/project.assets.json and obj/*.nuget.g.props record the resolved NuGetPackageRoot as an
# absolute path, so a container-relative one leaves every project un-buildable on the host afterwards
# ("NETSDK1064: package X was not found ... might have been deleted since NuGet restore") until
# something forces a re-restore. Same path in both places means obj/ stays valid either way, and the
# cache is shared instead of re-downloaded. DOTNET_CLI_HOME keeps the CLI's own state (~/.dotnet
# first-run sentinel, telemetry) inside the container, where HOME is writable.
mkdir -p "$HOME/.nuget/packages"
MOUNTS+=(-v "$HOME/.nuget:$HOME/.nuget")

# Zig's GLOBAL cache (compiler_rt, libc stubs, libc++, fetched packages) otherwise lives at
# $HOME/.cache/zig inside the image and dies with the --rm container, so every run rebuilt it from
# scratch — and much of that rebuild is serial, so it reads as a build that will not use the
# machine's cores. The engine's LOCAL .zig-cache already survives because it sits in the mounted
# source tree; this gives the global one the same treatment. Entries are keyed by compiler hash, so
# sharing the host's cache is safe even when the host zig and the image zig differ.
mkdir -p "$HOME/.cache/zig"
MOUNTS+=(-v "$HOME/.cache/zig:$HOME/.cache/zig")

# Same for cargo's downloaded crates: the image's CARGO_HOME holds the toolchain, but its registry
# would be re-fetched by every --rm run. Only the registry is shared — never bin/, which is the
# image's pinned toolchain.
mkdir -p "$HOME/.cargo/registry"
MOUNTS+=(-v "$HOME/.cargo/registry:/opt/cargo/registry")

# Run as the invoking user so nothing in the source tree comes back root-owned. Rootless podman needs
# keep-id for that; docker takes the uid directly.
AS_USER=(--user "$(id -u):$(id -g)")
[ "$(basename "$ENGINE")" = podman ] && AS_USER=(--userns=keep-id)

# On an SELinux host (Fedora, RHEL) a bind mount is unreadable from the container without either a
# relabel or this. `:z` is the usual answer, but it REWRITES the labels on the mounted directory —
# i.e. on the developer's source tree — and a shared label on a checkout is a side effect a build
# script has no business causing. Disabling confinement affects only this throwaway container.
SELINUX=()
[ -e /sys/fs/selinux ] && SELINUX=(--security-opt label=disable)

# A terminal gets an interactive container (shell, Ctrl-C); MSBuild's Exec and CI get plain pipes.
TTY=()
[ -t 0 ] && [ -t 1 ] && TTY=(-it)

exec "$ENGINE" run --rm "${TTY[@]}" "${MOUNTS[@]}" "${AS_USER[@]}" "${SELINUX[@]}" -w "$PWD" \
  -e "NUGET_PACKAGES=$HOME/.nuget/packages" -e DOTNET_CLI_HOME=/build-home \
  -e "ZIG_GLOBAL_CACHE_DIR=$HOME/.cache/zig" \
  "${EXTRA[@]}" "$IMAGE" "$@"
