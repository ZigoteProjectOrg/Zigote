#!/usr/bin/env bash
# Headless game export via the editor CLI. Used by CI (release.yml) and scripting.
#
#   build/export.sh <project.zigoteproj> [--rids osx-arm64,win-x64,...] [--mode jit|aot] [--out dir]
#
# Publishes the exported game per RID (NativeAOT only for host-OS RIDs; others fall back to
# self-contained JIT) and packages a .app (macOS) or zipped folder (Windows/Linux).
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [[ $# -lt 1 ]]; then
    echo "usage: build/export.sh <project.zigoteproj> [--rids a,b] [--mode jit|aot] [--out dir]" >&2
    exit 2
fi

export ZIGOTE_SDK="$repo_root"
exec dotnet run --project "$repo_root/Zigote.Editor/Zigote.Editor.csproj" -c Release -- --export "$@"
