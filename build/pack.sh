#!/usr/bin/env bash
#
# Pack the Zigote SDK: the managed libraries an app references, the CLI, and one native engine
# package per RID.
#
#   build/pack.sh [--version X.Y.Z] [--suffix rc.1] [--sign] [--push <source>] [rid ...]
#
# Examples:
#   build/pack.sh                                  # managed + the host RID's engine
#   build/pack.sh linux-x64 android-arm64          # managed + those two engines
#   build/pack.sh --version 0.2.0 --managed-only   # no Zig toolchain needed
#   build/pack.sh --suffix rc.1 --sign             # 0.1.0-rc.1, signed with $ZIGOTE_SIGN_CERT
#   build/pack.sh --sign --push nuget.org          # the full release path
#
# Output: artifacts/packages/*.nupkg plus SHA256SUMS — install a generated app against it with
#   dotnet nuget add source $PWD/artifacts/packages -n zigote-local
#
# Provenance, because these are meant to be published:
#   * the commit id is stamped into every assembly and .nuspec (SourceRevisionId);
#   * each engine package carries runtimes/<rid>/native/zigote-engine.json — version, commit and
#     the SHA-256 of every native file in it;
#   * SHA256SUMS covers the packages themselves (sha256sum -c SHA256SUMS to check a download);
#   * --sign signs every package with the release certificate and verifies the signatures before
#     anything is pushed. Signing needs one of:
#         ZIGOTE_SIGN_CERT + ZIGOTE_SIGN_CERT_PASSWORD   a PKCS#12 file, or
#         ZIGOTE_SIGN_FINGERPRINT                        a certificate in the machine store,
#     and honours ZIGOTE_TIMESTAMPER (default: DigiCert). A timestamp is what keeps a signature
#     valid after the certificate expires, so it is not optional here.
#
# Why this exists: an app used to reference a CHECKOUT, so "what is my app built against" had no
# answer, moving the app one directory up broke it, and CI had to reproduce a layout instead of
# restoring a version. Everything here ships under one $(ZigoteVersion).
#
# The managed half is packed with -p:ZigoteManagedPack=true, which skips the native build entirely:
# a machine that only publishes libraries needs no Zig, no NDK and no Android sysroot. The engine
# packages are the half that does, and each is built one RID at a time because zig-out holds
# exactly one libzigote.
set -euo pipefail
cd "$(dirname "$0")/.."

version=""
suffix=""
managed_only=false
sign=false
push=""
rids=()
while [ $# -gt 0 ]; do
  case "$1" in
    --version) version="$2"; shift 2 ;;
    --suffix) suffix="$2"; shift 2 ;;
    --sign) sign=true; shift ;;
    --push) push="$2"; shift 2 ;;
    --managed-only) managed_only=true; shift ;;
    -h|--help) sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) rids+=("$1"); shift ;;
  esac
done

props=(-c Release -p:IsPackable=true)
[ -n "$version" ] && props+=("-p:ZigoteVersion=$version")
[ -n "$suffix" ] && props+=("-p:ZigoteVersionSuffix=$suffix")
# The commit these artifacts were built from, into every assembly, .nuspec and engine manifest.
# A dirty tree is marked as such rather than claiming to be the commit it is merely near.
commit="$(git rev-parse HEAD 2>/dev/null || echo unknown)"
git diff --quiet 2>/dev/null || commit="$commit-dirty"
props+=("-p:SourceRevisionId=$commit")

# The host RID by default: packing the engine for a platform you cannot cross-compile to is a
# failed build, not a useful default.
if [ ${#rids[@]} -eq 0 ] && [ "$managed_only" = false ]; then
  case "$(uname -s)-$(uname -m)" in
    Linux-x86_64)  rids=(linux-x64) ;;
    Linux-aarch64) rids=(linux-arm64) ;;
    Darwin-arm64)  rids=(osx-arm64) ;;
    Darwin-x86_64) rids=(osx-x64) ;;
    *) echo "unknown host — name the RIDs to pack" >&2; exit 1 ;;
  esac
fi

# The projects an app references, plus everything they reference. NuGet turns a ProjectReference
# into a package DEPENDENCY, so a closure member that is not packed is a package that cannot
# restore — and the failure lands on the user, at restore time, naming a package nobody built.
# Walked rather than listed: a list would be right until the next `dotnet add reference`.
roots=(
  Zigote.UI/Zigote.UI.csproj
  Zigote.UI.Material/Zigote.UI.Material.csproj
  Zigote.UI.DevTools/Zigote.UI.DevTools.csproj
  Zigote.Runtime/Zigote.Runtime.csproj
)
mapfile -t managed < <(python3 build/pack/closure.py "${roots[@]}")
managed+=(Zigote.Cli/Zigote.Cli.csproj)

rm -rf artifacts/packages
for project in "${managed[@]}"; do
  echo "==> pack $project"
  dotnet pack "$project" "${props[@]}" -p:ZigoteManagedPack=true \
    -p:PackageOutputPath="$PWD/artifacts/packages" --nologo -v q
done

if [ "$managed_only" = false ]; then
  for rid in "${rids[@]}"; do
    echo "==> pack engine $rid"
    dotnet pack build/pack/Zigote.Engine.Runtime.csproj "${props[@]}" \
      "-p:ZigTargetRid=$rid" -p:PackageOutputPath="$PWD/artifacts/packages" --nologo -v q
  done
fi

# The mobile heads' non-assembly halves: SDL's Java sources and the hot-reload switch for Android,
# the fonts as bundle resources for iOS. RID independent, so each is packed once — and packed
# whenever that platform is in play at all.
if printf '%s\n' "${rids[@]:-}" | grep -q android || [ "$managed_only" = true ]; then
  echo "==> pack Zigote.Android.Support"
  dotnet pack build/pack/Zigote.Android.Support.csproj "${props[@]}" \
    -p:PackageOutputPath="$PWD/artifacts/packages" --nologo -v q
fi

if printf '%s\n' "${rids[@]:-}" | grep -q ios || [ "$managed_only" = true ]; then
  echo "==> pack Zigote.iOS.Support"
  dotnet pack build/pack/Zigote.iOS.Support.csproj "${props[@]}" \
    -p:PackageOutputPath="$PWD/artifacts/packages" --nologo -v q
fi

# Sign first, then hash: SHA256SUMS should describe exactly what is published, signature and all.
sign_packages() {
  local timestamper="${ZIGOTE_TIMESTAMPER:-http://timestamp.digicert.com}"
  local args=(--timestamper "$timestamper")
  if [ -n "${ZIGOTE_SIGN_CERT:-}" ]; then
    args+=(--certificate-path "$ZIGOTE_SIGN_CERT")
    [ -n "${ZIGOTE_SIGN_CERT_PASSWORD:-}" ] && args+=(--certificate-password "$ZIGOTE_SIGN_CERT_PASSWORD")
  elif [ -n "${ZIGOTE_SIGN_FINGERPRINT:-}" ]; then
    args+=(--certificate-fingerprint "$ZIGOTE_SIGN_FINGERPRINT")
  else
    echo "--sign needs ZIGOTE_SIGN_CERT (a .pfx) or ZIGOTE_SIGN_FINGERPRINT (a cert in the store)." >&2
    exit 2
  fi
  for package in artifacts/packages/*.nupkg; do
    echo "==> sign $(basename "$package")"
    dotnet nuget sign "$package" "${args[@]}"
  done
  # Verifying our own output is not ceremony: an unsigned package in the set is exactly the one
  # that gets pushed by accident, and the feed rejects it only after the others are already public.
  dotnet nuget verify artifacts/packages/*.nupkg
}

[ "$sign" = true ] && sign_packages

( cd artifacts/packages && sha256sum ./*.nupkg > SHA256SUMS )

if [ -n "$push" ]; then
  if [ -z "${NUGET_API_KEY:-}" ]; then
    echo "--push needs NUGET_API_KEY in the environment." >&2
    exit 2
  fi
  if [ "$sign" = false ]; then
    echo "refusing to push unsigned packages — pass --sign. A published SDK that cannot be" >&2
    echo "verified after the fact is one nobody can tell from a tampered copy." >&2
    exit 2
  fi
  for package in artifacts/packages/*.nupkg; do
    echo "==> push $(basename "$package")"
    dotnet nuget push "$package" --source "$push" --api-key "$NUGET_API_KEY" --skip-duplicate
  done
fi

echo
ls -1 artifacts/packages/*.nupkg
echo "  checksums: artifacts/packages/SHA256SUMS"
