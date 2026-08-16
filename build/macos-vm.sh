#!/usr/bin/env bash
#
# The macOS build machine, as a container: a dockur/macos VM (QEMU + KVM + OpenCore) that
# build/publish.sh --macos-vm publishes inside. The Linux counterpart of this file is
# build/Dockerfile.linux; this one is a VM rather than an image for one reason — a macOS toolchain
# cannot be installed from a Dockerfile. Xcode's SDK is the whole point (zig needs --sysroot for the
# Apple frameworks SDL/miniaudio/Cocoa link against, and ilc needs clang/ld64 to link a Mach-O), and
# Apple ships it only to a running macOS.
#
#   build/macos-vm.sh up             # create/start; exits 0 only when SSH answers
#   build/macos-vm.sh provision      # Xcode CLT + .NET SDK + zig, idempotent
#   build/macos-vm.sh ssh [cmd...]   # one command (or a shell) in the VM
#   build/macos-vm.sh status         # one line per fact, exit 0 iff ready to build
#   build/macos-vm.sh down           # graceful shutdown (macOS gets 120s)
#   build/macos-vm.sh snapshot FILE  # tar the provisioned disk — the CI seed, see below
#   build/macos-vm.sh restore FILE   # untar it onto a fresh machine
#   build/macos-vm.sh destroy        # remove the container AND the disk image
#
# Every command is non-interactive and never prompts: progress goes to stderr, results to stdout, and
# a non-zero exit always means "not usable as a build machine". The only exception is the first
# install (below), which Apple provides no unattended path for.
#
# LICENCE: macOS is licensed for Apple-branded hardware only. This is a local test rig for producing
# and smoke-testing osx-* artifacts; shipping builds should come off a real Mac (or a hosted one) in
# CI — see .github/workflows/release.yml.
#
# ARCHITECTURE: KVM virtualizes, it does not emulate, so an x86_64 host runs an INTEL macOS guest.
# That guest publishes osx-x64 natively and osx-arm64 as a same-OS cross build (JIT always; AOT via
# the SDK's host/target ILCompiler split, which needs no Rosetta because ilc itself stays x64).
# On an arm64 Linux host dockur/macos does not work at all — there is no OpenCore path for it.
#
# FIRST INSTALL — the one hands-on step. Apple's installer is a GUI with no unattended mode, so on an
# empty disk `up` boots it and waits. Open http://127.0.0.1:8006 and:
#
#   1. Disk Utility → erase the largest QEMU HARDDISK as APFS → quit.
#   2. Reinstall macOS onto it → region/keyboard, skip Apple ID, skip Migration Assistant.
#   3. Create the account. Its short name must match $MACOS_USER (default: this host's username),
#      because that is who publish.sh logs in as.
#   4. Terminal (⌘-Space → "terminal"): `sudo systemsetup -setremotelogin on`
#   5. Back on the host: `ssh-copy-id -i "$MACOS_STORAGE/id_ed25519.pub" -p 2222 $USER@127.0.0.1`
#
# That is the last GUI step ever. `provision`, then `snapshot build-mac.tar` — a build system seeds
# itself with `restore build-mac.tar && up` and never sees the installer at all.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

NAME="${MACOS_VM_NAME:-zigote-macos}"
IMAGE="${MACOS_VM_IMAGE:-docker.io/dockurr/macos}"
# 15 = Sequoia, the last release Apple shipped for Intel. 26 (Tahoe) is Apple-silicon-only and dockur
# marks it unsupported, so it is not a default worth offering.
VERSION="${MACOS_VERSION:-15}"
RAM="${MACOS_RAM:-12G}"
CORES="${MACOS_CORES:-8}"
DISK="${MACOS_DISK:-128G}"
SSH_PORT="${MACOS_SSH_PORT:-2222}"
WEB_PORT="${MACOS_WEB_PORT:-8006}"
MACOS_USER="${MACOS_USER:-$(id -un)}"
# How long `up` waits for SSH before calling it a failure. The default covers a cold boot; a build
# system restoring a snapshot onto slow storage may want more.
BOOT_TIMEOUT="${MACOS_BOOT_TIMEOUT:-300}"
# Not under artifacts/ and not in the repo: this is a ~100 GB disk image that must outlive both a
# `rm -rf artifacts` and a fresh clone.
STORAGE="${MACOS_STORAGE:-${XDG_DATA_HOME:-$HOME/.local/share}/zigote/macos-vm}"
KEY="$STORAGE/id_ed25519"

# Pinned in exactly one place — build/Dockerfile.linux — so the two build machines cannot drift into
# publishing artifacts from different toolchains. Override to test a bump before committing it.
DOTNET_CHANNEL="${DOTNET_CHANNEL:-$(sed -n 's/^ARG DOTNET_CHANNEL=//p' "$ROOT/build/Dockerfile.linux")}"
ZIG_VERSION="${ZIG_VERSION:-$(sed -n 's/^ARG ZIG_VERSION=//p' "$ROOT/build/Dockerfile.linux")}"

ENGINE="$(command -v podman || command -v docker)" ||
  { echo "macos-vm needs podman or docker on PATH" >&2; exit 2; }

# Progress is stderr, so `$(macos-vm.sh ssh uname -m)` and friends stay pipeable.
say() { echo "$@" >&2; }

# BatchMode: nothing here may ever block on a password prompt in a pipeline. UserKnownHostsFile
# is dropped because the guest regenerates its host key on reinstall, and a rebuilt test VM tripping
# the man-in-the-middle warning would be noise, not a signal — the endpoint is a forwarded port on
# loopback with a key that lives beside the disk image.
SSH_ARGS="-p $SSH_PORT -i $KEY -o BatchMode=yes -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR"
SSH_HOST="$MACOS_USER@127.0.0.1"

exists()  { "$ENGINE" inspect "$NAME" >/dev/null 2>&1; }
running() { [ "$("$ENGINE" inspect -f '{{.State.Running}}' "$NAME" 2>/dev/null)" = true ]; }
ssh_ok()  { ssh $SSH_ARGS -o ConnectTimeout=4 "$SSH_HOST" true 2>/dev/null; }
# Provisioned means the toolchain answers, not that provision once ran — a half-finished CLT install
# is the failure this is here to catch.
tools_ok() { ssh $SSH_ARGS -o ConnectTimeout=8 "$SSH_HOST" \
               'xcrun --show-sdk-path >/dev/null 2>&1 && command -v dotnet >/dev/null && command -v zig >/dev/null' 2>/dev/null; }

require_ssh() {
  ssh_ok && return 0
  say "The macOS VM is not answering on port $SSH_PORT."
  if ! exists; then say "  Not created. Run: build/macos-vm.sh up"
  elif ! running; then say "  Not running. Run: build/macos-vm.sh up"
  else say "  Running but unreachable — finish the install at http://127.0.0.1:$WEB_PORT (see the header of $0)."
  fi
  exit 1
}

cmd_up() {
  [ -w /dev/kvm ] || { say "/dev/kvm is not writable — KVM is what makes this a build machine rather than a 40x-slower emulator."; exit 1; }
  mkdir -p "$STORAGE"
  # The key is the VM's only credential and lives beside its disk, so `destroy` takes both and
  # `snapshot` carries both to the next machine.
  [ -f "$KEY" ] || ssh-keygen -q -t ed25519 -N '' -C "zigote-macos-vm" -f "$KEY"

  if ! exists; then
    # --device /dev/net/tun + NET_ADMIN: dockur's preferred networking. Without them it falls back to
    # user-mode (slirp/passt), which still forwards SSH — hence the tun device is passed only when the
    # host actually has one, rather than making an otherwise-working setup fail on its absence.
    TUN=(); [ -w /dev/net/tun ] && TUN=(--device=/dev/net/tun --cap-add NET_ADMIN)
    # :Z relabels for SELinux. Safe here (unlike the source-tree mounts in publish.sh --container,
    # which is why that one disables confinement instead): this directory exists only for the VM.
    VOL="$STORAGE:/storage"; [ -e /sys/fs/selinux ] && VOL="$VOL:Z"
    # Port 22 in the guest is forwarded to the container by dockur out of the box (qemu-docker's
    # user-mode hostfwd defaults include it); this publishes it on the host as $SSH_PORT.
    # DISK_CACHE: dockur defaults the data disk to cache=none (direct I/O, host page cache bypassed),
    # which is right for a VM whose contents matter and wrong for this one. The macOS installer is
    # millions of small synchronous writes, and measured on this host that default gave 2.5 MB/s and
    # a multi-hour install. writeback lets the host absorb them; the cost is that a host crash can
    # corrupt the image, which for a build machine means `destroy` and restore a snapshot.
    # aio=native is invalid with writeback (qemu-docker asserts this), hence threads alongside it.
    "$ENGINE" run -d --name "$NAME" \
      -e "VERSION=$VERSION" -e "RAM_SIZE=$RAM" -e "CPU_CORES=$CORES" -e "DISK_SIZE=$DISK" \
      -e "DISK_CACHE=${MACOS_DISK_CACHE:-writeback}" -e "DISK_IO=${MACOS_DISK_IO:-threads}" \
      -p "$WEB_PORT:8006" -p "$SSH_PORT:22" \
      --device=/dev/kvm "${TUN[@]}" \
      -v "$VOL" --stop-timeout 120 \
      "$IMAGE" >/dev/null
    say "Created $NAME (macOS $VERSION · $CORES cores · $RAM · $DISK)."
  elif ! running; then
    "$ENGINE" start "$NAME" >/dev/null
    say "Started $NAME."
  fi

  say "Waiting up to ${BOOT_TIMEOUT}s for SSH on $SSH_PORT..."
  deadline=$((SECONDS + BOOT_TIMEOUT))
  while [ "$SECONDS" -lt "$deadline" ]; do
    if ssh_ok; then say "Ready: ssh on $SSH_PORT."; return 0; fi
    sleep 5
  done
  # Non-zero on "not usable", so a pipeline stops here instead of running a publish that cannot work.
  # On a first run this is the expected outcome: the VM is sitting in Apple's installer.
  say "No SSH after ${BOOT_TIMEOUT}s."
  say "  If this disk has never been installed, that is expected — do the first install at"
  say "  http://127.0.0.1:$WEB_PORT (steps in the header of $0), then re-run \`up\`."
  exit 1
}

cmd_provision() {
  require_ssh

  # Root is needed once, for the CLT install. MACOS_SUDO_PASS keeps that non-interactive; without it
  # the guest must have NOPASSWD, and a build system gets a precise error instead of a hung prompt.
  sudo_prefix='sudo'
  [ -n "${MACOS_SUDO_PASS:-}" ] && sudo_prefix="printf '%s\n' \"\$MACOS_SUDO_PASS\" | sudo -S"

  ssh $SSH_ARGS "$SSH_HOST" \
    "DOTNET_CHANNEL='$DOTNET_CHANNEL' ZIG_VERSION='$ZIG_VERSION' MACOS_SUDO_PASS='${MACOS_SUDO_PASS:-}' SUDO='$sudo_prefix' bash -s" <<'PROVISION'
set -eu

# 1. Xcode Command Line Tools — the SDK, and the reason this VM exists. `xcode-select --install` opens
# a GUI dialog nobody can click over SSH; the sentinel file below is the documented headless path (it
# makes softwareupdate list the CLT package, which is otherwise hidden from it).
if ! xcode-select -p >/dev/null 2>&1; then
  echo "── Xcode Command Line Tools" >&2
  sentinel=/tmp/.com.apple.dt.CommandLineTools.installondemand.in-progress
  touch "$sentinel"
  label="$(softwareupdate -l 2>/dev/null | awk -F'Label: ' '/Label: Command Line Tools/ {print $2}' | tail -1)"
  rm -f "$sentinel"
  [ -n "$label" ] || { echo "no Command Line Tools package offered by softwareupdate" >&2; exit 1; }
  if ! eval "$SUDO -p '' true" 2>/dev/null; then
    echo "sudo needs a password: set MACOS_SUDO_PASS, or give the account NOPASSWD in the guest." >&2
    exit 1
  fi
  eval "$SUDO softwareupdate -i '$label' --verbose" >&2
fi
# Both the zig sysroot (build/Zigote.Native.targets → ZigMacosSysroot) and ilc's linker resolve
# through xcrun, so a CLT that installed but did not register is a failure worth catching here rather
# than 20 minutes into a publish.
xcrun --show-sdk-path >/dev/null

# 2 & 3. Toolchain, user-local: neither needs root, and keeping them out of /usr/local means a
# re-provision is a delete of one directory rather than a package-manager argument.
mkdir -p "$HOME/.local/bin" "$HOME/.local/opt"

if ! "$HOME/.dotnet/dotnet" --version >/dev/null 2>&1; then
  echo "── .NET SDK $DOTNET_CHANNEL" >&2
  curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel "$DOTNET_CHANNEL" --install-dir "$HOME/.dotnet" >&2
fi

if [ "$("$HOME/.local/bin/zig" version 2>/dev/null || true)" != "$ZIG_VERSION" ]; then
  echo "── zig $ZIG_VERSION" >&2
  arch="$(uname -m)"
  rm -rf "$HOME/.local/opt/zig-$arch-macos-$ZIG_VERSION"
  curl -fsSL "https://ziglang.org/download/$ZIG_VERSION/zig-$arch-macos-$ZIG_VERSION.tar.xz" \
    | tar -xf - -C "$HOME/.local/opt"
  ln -sf "$HOME/.local/opt/zig-$arch-macos-$ZIG_VERSION/zig" "$HOME/.local/bin/zig"
fi

# .zshenv, not .zprofile: `ssh host 'cmd'` runs a NON-login shell, which reads only this file — and
# every publish from the Linux side arrives exactly that way.
grep -q 'zigote build toolchain' "$HOME/.zshenv" 2>/dev/null || cat >> "$HOME/.zshenv" <<'ENVRC'
# zigote build toolchain (build/macos-vm.sh provision)
export PATH="$HOME/.dotnet:$HOME/.local/bin:$PATH"
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
ENVRC
PROVISION

  cmd_status
}

# The disk image plus the SSH key IS the build machine. Tarring it is what turns a one-time GUI
# install into something a build system can provision from cold — restore, up, publish.
cmd_snapshot() {
  [ -n "${1:-}" ] || { say "usage: macos-vm.sh snapshot FILE.tar.zst"; exit 2; }
  running && { say "Shut the VM down first (macos-vm.sh down) — a snapshot of a live disk is a corrupt one."; exit 1; }
  say "Packing $STORAGE → $1 (this is the whole disk image; expect tens of GB)"
  tar -C "$STORAGE" -caf "$1" .
  say "Done: $(du -h "$1" | cut -f1)"
}

cmd_restore() {
  [ -r "${1:-}" ] || { say "usage: macos-vm.sh restore FILE.tar.zst"; exit 2; }
  exists && { say "$NAME already exists — destroy it first."; exit 1; }
  mkdir -p "$STORAGE"
  tar -C "$STORAGE" -xaf "$1"
  say "Restored into $STORAGE. Next: build/macos-vm.sh up"
}

cmd_status() {
  if ! exists; then echo "container: absent"; exit 1; fi
  echo "container: $("$ENGINE" inspect -f '{{.State.Status}}' "$NAME")"
  if ! ssh_ok; then echo "ssh: unreachable on $SSH_PORT"; exit 1; fi
  echo "ssh: up on $SSH_PORT"
  if ! tools_ok; then echo "toolchain: incomplete (run: build/macos-vm.sh provision)"; exit 1; fi
  ssh $SSH_ARGS "$SSH_HOST" '
    echo "os: $(sw_vers -productName) $(sw_vers -productVersion) $(uname -m)"
    echo "sdk: $(xcrun --show-sdk-version) at $(xcrun --show-sdk-path)"
    echo "dotnet: $(dotnet --version)"
    echo "zig: $(zig version)"'
}

case "${1:-}" in
  up)        cmd_up ;;
  provision) cmd_provision ;;
  ssh)       shift; require_ssh; exec ssh $SSH_ARGS "$SSH_HOST" "$@" ;;
  # `eval "$(macos-vm.sh env)"` — how publish.sh --macos-vm drives rsync without re-deriving the
  # port/key/user split that everything above already agreed on.
  env)       echo "MACOS_SSH_ARGS='$SSH_ARGS'"; echo "MACOS_SSH_HOST='$SSH_HOST'" ;;
  status)    cmd_status ;;
  down)      "$ENGINE" stop -t 120 "$NAME" >/dev/null && say "Stopped $NAME." ;;
  snapshot)  cmd_snapshot "${2:-}" ;;
  restore)   cmd_restore "${2:-}" ;;
  destroy)   "$ENGINE" rm -f "$NAME" >/dev/null 2>&1 || true
             rm -rf "$STORAGE"; say "Removed $NAME and $STORAGE." ;;
  *)         sed -n '2,43p' "$0" >&2; exit 2 ;;
esac
