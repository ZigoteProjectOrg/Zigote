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

RIDS=()
while [ $# -gt 0 ]; do
  case "$1" in
    --project) PROJECT="$2"; shift 2 ;;
    --name)    NAME="$2";    shift 2 ;;
    --aot)     AOT=1;        shift ;;
    -h|--help) sed -n '2,24p' "$0"; exit 0 ;;
    -*)        echo "unknown option: $1" >&2; exit 2 ;;
    *)         RIDS+=("$1"); shift ;;
  esac
done

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
  # native lib (drops the Assimp model importer) for 2D-only apps like Signals. FONT_SUBSET_TOOL
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
  else
    pub_args+=(--self-contained true -p:PublishTrimmed=false -p:PublishSingleFile=false)
  fi
  if dotnet publish "$PROJECT" "${pub_args[@]}" \
       ${ENABLE_3D:+-p:Enable3D=$ENABLE_3D} \
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
