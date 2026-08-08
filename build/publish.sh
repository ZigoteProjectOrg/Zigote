#!/usr/bin/env bash
#
# Per-RID self-contained release publish of a Zigote app (the editor by default; any app via --project).
#
#   build/publish.sh [--project <csproj>] [--name <artifact-name>] [--aot] [rid ...]
#
# Examples:
#   build/publish.sh                                          # editor, RIDs for the current host
#   build/publish.sh osx-arm64 win-x64 linux-x64             # editor, all desktop RIDs (cross-built)
#   build/publish.sh --project ~/ZigoteProjects/Signals/Signals.csproj --name signals win-x64
#   build/publish.sh --aot --project Zigote.UI.Gallery/Zigote.UI.Gallery.csproj osx-arm64  # NativeAOT
#
# Default: each RID → a self-contained, NON-trimmed JIT bundle (the engine's reflection-based scripting/
# serialization is trim-unsafe) with the native libzigote (+ wgpu_native.dll on Windows) and the app's
# fonts, zipped to artifacts/<name>-<rid>.zip.
#
# --aot: NativeAOT publish (-p:ZigoteAot=true → PublishAot). ONLY for apps that opt in via
# build/Zigote.Aot.targets AND do no runtime assembly loading — i.e. the galleries, NOT the editor (it
# compiles+loads user scripts into a collectible ALC, which NativeAOT cannot do). AOT is host-RID only:
# ilc has no cross-OS codegen, so publish osx-* from macOS, linux-* from Linux, win-* from Windows.
#
# --single-file: bundle a JIT publish into ONE executable (a Windows .exe, or a single Linux binary)
# that self-extracts on first run. Ignored for --aot, which is already one native binary.
#
# --container: re-run the whole publish inside build/Dockerfile.linux (Debian 12) instead of on this
# machine, and only for that. A binary inherits the glibc of the machine that built it as a hard
# load-time floor — so a build on a rolling-release host produces a libzigote.so and (worse, because
# there is no -Dtarget for it) a NativeAOT binary that will not start inside a Flatpak runtime or on
# any LTS distro. Linux RIDs only; podman or docker required.
#
# Cross-compilation (the native lib is built per-RID by build/Zigote.Native.targets):
#   * From any host: osx-arm64/osx-x64, linux-x64, and win-x64 — Windows cross uses the GNU ABI (Zig
#     bundles MinGW). MSVC-ABI cross needs the MSVC SDK so it's not attempted; a native Windows host
#     builds the msvc ABI fine. Per-OS coverage / signing → the CI matrix in .github/workflows/release.yml.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/artifacts"
CONFIG="${CONFIG:-Release}"
PROJECT="$ROOT/Zigote.Editor/Zigote.Editor.csproj"
NAME=""
AOT=0
CONTAINER=0
SINGLE_FILE=0

ARGV=("$@")
RIDS=()
while [ $# -gt 0 ]; do
  case "$1" in
    --project)   PROJECT="$2"; shift 2 ;;
    --name)      NAME="$2";    shift 2 ;;
    --aot)       AOT=1;        shift ;;
    --container) CONTAINER=1;  shift ;;
    --single-file) SINGLE_FILE=1; shift ;;
    -h|--help)   sed -n '2,31p' "$0"; exit 0 ;;
    -*)          echo "unknown option: $1" >&2; exit 2 ;;
    *)           RIDS+=("$1"); shift ;;
  esac
done

# ── containerized re-entry ────────────────────────────────────────────────────────────────────────
# Everything below this block runs identically inside and outside the container; --container only
# changes *where*. Re-exec rather than duplicating the publish logic behind a second code path.
if [ "$CONTAINER" -eq 1 ]; then
  ENGINE="$(command -v podman || command -v docker)" ||
    { echo "--container needs podman or docker on PATH" >&2; exit 2; }
  IMAGE="${ZIGOTE_BUILD_IMAGE:-zigote-build-linux}"

  # Build once; rebuild only when the Dockerfile changes (the layer cache makes a no-op rebuild cheap,
  # and skipping it entirely would silently keep a stale toolchain).
  "$ENGINE" build -q -t "$IMAGE" -f "$ROOT/build/Dockerfile.linux" "$ROOT/build" >/dev/null ||
    { echo "failed to build $IMAGE" >&2; exit 1; }

  # Mount the engine and the app repo at their real paths so ZigoteRoot, relative ProjectReferences and
  # the artifacts directory all resolve to the same strings they would on the host. For an in-tree app
  # (the editor, the galleries) these are the same directory, hence the dedupe.
  APP_ROOT="$(cd "$(dirname "$(dirname "$PROJECT")")" && pwd)"
  MOUNTS=(-v "$ROOT:$ROOT")
  [ "$APP_ROOT" != "$ROOT" ] && MOUNTS+=(-v "$APP_ROOT:$APP_ROOT")
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
  # $HOME/.cache/zig inside the image and dies with the --rm container, so every leg of a multi-arch
  # publish rebuilt it from scratch — and much of that rebuild is serial, so it reads as a build that
  # will not use the machine's cores. The engine's LOCAL .zig-cache already survives because it sits
  # in the mounted source tree; this gives the global one the same treatment. Entries are keyed by
  # compiler hash, so sharing the host's cache is safe even when the host zig and the image zig differ.
  mkdir -p "$HOME/.cache/zig"
  MOUNTS+=(-v "$HOME/.cache/zig:$HOME/.cache/zig")

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

  echo "Container build in $IMAGE (glibc floor from build/Dockerfile.linux)"
  # Drop --container from the forwarded args, or the re-exec would recurse.
  INNER=()
  for a in "${ARGV[@]}"; do [ "$a" = --container ] || INNER+=("$a"); done
  exec "$ENGINE" run --rm "${MOUNTS[@]}" "${AS_USER[@]}" "${SELINUX[@]}" -w "$ROOT" \
    -e "NUGET_PACKAGES=$HOME/.nuget/packages" -e DOTNET_CLI_HOME=/build-home \
    -e "ZIG_GLOBAL_CACHE_DIR=$HOME/.cache/zig" \
    -e "CONFIG=$CONFIG" ${ENABLE_3D:+-e "ENABLE_3D=$ENABLE_3D"} \
    ${PHYSICS_3D:+-e "PHYSICS_3D=$PHYSICS_3D"} \
    ${ZIG_OPTIMIZE:+-e "ZIG_OPTIMIZE=$ZIG_OPTIMIZE"} \
    ${ZIG_CPU:+-e "ZIG_CPU=$ZIG_CPU"} \
    ${FONT_SUBSET_TOOL:+-e "FONT_SUBSET_TOOL=$FONT_SUBSET_TOOL"} \
    "$IMAGE" "$ROOT/build/publish.sh" "${INNER[@]}"
fi

# Default artifact name from the project filename (Zigote.Editor.csproj → zigote-editor, Signals → signals).
[ -z "$NAME" ] && NAME="$(basename "$PROJECT" .csproj | tr '[:upper:].' '[:lower:]-')"

host_rids() {
  case "$(uname -s)" in
    Darwin) echo "osx-arm64 osx-x64" ;;
    Linux)  echo "linux-x64" ;;
    *)      echo "win-x64" ;;
  esac
}
[ ${#RIDS[@]} -eq 0 ] && read -ra RIDS <<< "$(host_rids)"

# NativeAOT (ilc) has no cross-OS codegen — a RID's OS must match the host. (Cross-ARCH on the same OS,
# e.g. osx-x64 from osx-arm64, works.) Map host OS → RID prefix so we can skip mismatched RIDs.
host_os_prefix() { case "$(uname -s)" in Darwin) echo osx ;; Linux) echo linux ;; *) echo win ;; esac; }

MODE="self-contained JIT"; [ "$AOT" -eq 1 ] && MODE="NativeAOT"
echo "Publishing $(basename "$PROJECT") as '$NAME' [$CONFIG · $MODE] for: ${RIDS[*]}"
mkdir -p "$OUT"
fail=0
for rid in "${RIDS[@]}"; do
  echo "──────────── $rid ────────────"
  if [ "$AOT" -eq 1 ] && [[ "$rid" != "$(host_os_prefix)"* ]]; then
    echo "  ⚠️  skipped: NativeAOT can't cross-compile to a different OS than the $(uname -s) host."
    fail=1
    continue
  fi
  dest="$OUT/$NAME-$rid"
  rm -rf "$dest"
  # ZigTargetRid forces the native cross-build (in the referenced Zigote.Core) to this RID — a plain -r
  # doesn't reliably reach a ProjectReference's evaluation. ENABLE_3D=false (optional) builds the lean
  # native lib (drops the Assimp model importer) for 2D-only apps like Signals; PHYSICS_3D=false drops
  # the Jolt FFI alongside it. Both must be passed on the command line, not set in the app's .csproj:
  # the native build lives in the referenced Zigote.Core, and only a global property reaches it.
  #
  # ZIG_OPTIMIZE (optional) overrides the native optimize mode, which Zigote.Native.targets otherwise
  # pins to ReleaseFast for a Release build. An app that feeds the engine's decoders untrusted input —
  # a media player parsing arbitrary tags, cover art and network audio — wants ReleaseSafe: under
  # ReleaseFast Zig drops bounds and overflow checks, so a malformed image or stream turns a would-be
  # panic into memory corruption. Same escape hatch as the flags above, same reason it lives here.
  #
  # ZIG_CPU (optional) overrides the native CPU floor, which Zigote.Native.targets otherwise pins to
  # x86_64_v2 / aarch64 baseline. Only raise it for an artifact whose audience is known (or for a
  # benchmark): the value is compiled in, not runtime-dispatched, so anything older than the named CPU
  # dies with SIGILL at startup. Any zig CPU name works, `native` included — which is exactly the
  # build-machine-detected behaviour the pin exists to prevent, so keep it out of shipped artifacts.
  # FONT_SUBSET_TOOL
  # (optional) overrides the publish-time subsetter; apps that bundle pre-stripped fonts ignore it.
  # DebugType=none strips PDBs from the release bundle (smaller, and no build-machine source paths
  # leak into shipped binaries — .NET keeps portable PDBs in Release by default). Dev `dotnet build`
  # is unaffected, so day-to-day stack traces still have file:line.
  #
  # AOT vs JIT differ only in the trimming/single-file switches: NativeAOT is inherently self-contained
  # and trimmed (ZigoteAot=true → PublishAot in Zigote.Aot.targets), so those flags are dropped there.
  pub_args=(-c "$CONFIG" -r "$rid" -p:DebugType=none -p:DebugSymbols=false -p:ZigTargetRid="$rid")
  if [ "$AOT" -eq 1 ]; then
    pub_args+=(-p:ZigoteAot=true)
  elif [ "$SINGLE_FILE" -eq 1 ]; then
    # One executable that unpacks itself to a temp dir on first run. IncludeAllContentForSelfExtract
    # (not merely IncludeNativeLibrariesForSelfExtract) because the engine needs more than DLLs next
    # to the binary: zigote.dll, wgpu_native.dll AND the Fonts/ directory must all be present before
    # the first frame, and content files are otherwise left loose beside the exe.
    pub_args+=(--self-contained true -p:PublishTrimmed=false -p:PublishSingleFile=true
               -p:IncludeAllContentForSelfExtract=true)
  else
    pub_args+=(--self-contained true -p:PublishTrimmed=false -p:PublishSingleFile=false)
  fi
  if dotnet publish "$PROJECT" "${pub_args[@]}" \
       ${ENABLE_3D:+-p:Enable3D=$ENABLE_3D} \
       ${PHYSICS_3D:+-p:EnablePhysics3D=$PHYSICS_3D} \
       ${ZIG_OPTIMIZE:+-p:ZigOptimize=$ZIG_OPTIMIZE} \
       ${ZIG_CPU:+-p:ZigCpu=$ZIG_CPU} \
       ${FONT_SUBSET_TOOL:+-p:FontSubsetTool=$FONT_SUBSET_TOOL} \
       -o "$dest"; then
    ( cd "$OUT" && rm -f "$NAME-$rid.zip" && zip -qr "$NAME-$rid.zip" "$(basename "$dest")" )
    echo "  ✅ $rid → $OUT/$NAME-$rid.zip"
  else
    echo "  ❌ $rid failed (see log above)."
    fail=1
  fi
done
[ $fail -eq 0 ] && echo "All targets published." || echo "Some targets failed (see above)."
exit $fail
