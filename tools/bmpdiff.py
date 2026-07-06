#!/usr/bin/env python3
"""Compare two 24-bit BMPs and report pixel differences.

Renderer regression gate for the pure-wgpu work: `ZIGOTE_SHOT` dumps a 24-bit
BMP; this reports max-abs channel error and the count/fraction of differing
pixels between two captures. Exit 0 = within tolerance, 1 = differs, 2 = error.

Usage: bmpdiff.py A.bmp B.bmp [--tol N] [--maxpix N]
  --tol N      per-channel abs-error allowed before a pixel counts as "differing" (default 0)
  --maxpix N   allow up to N differing pixels and still exit 0 (default 0)
"""
import struct
import sys


def load_bmp(path):
    with open(path, "rb") as f:
        data = f.read()
    if data[:2] != b"BM":
        raise ValueError(f"{path}: not a BMP")
    pixel_offset = struct.unpack_from("<I", data, 10)[0]
    width = struct.unpack_from("<i", data, 18)[0]
    height = struct.unpack_from("<i", data, 22)[0]
    bpp = struct.unpack_from("<H", data, 28)[0]
    if bpp != 24:
        raise ValueError(f"{path}: expected 24-bit, got {bpp}")
    return width, abs(height), data[pixel_offset:]


def main(argv):
    args = [a for a in argv if not a.startswith("--")]
    opts = {a.split("=")[0]: (a.split("=")[1] if "=" in a else None) for a in argv if a.startswith("--")}
    if len(args) != 2:
        print(__doc__)
        return 2
    tol = int(opts.get("--tol") or 0)
    maxpix = int(opts.get("--maxpix") or 0)
    try:
        (w0, h0, p0), (w1, h1, p1) = load_bmp(args[0]), load_bmp(args[1])
    except (OSError, ValueError) as e:
        print(f"bmpdiff: {e}", file=sys.stderr)
        return 2
    if (w0, h0) != (w1, h1):
        print(f"bmpdiff: dimension mismatch {w0}x{h0} vs {w1}x{h1}", file=sys.stderr)
        return 1
    n = min(len(p0), len(p1))
    max_err = 0
    diff_bytes = 0
    for i in range(n):
        d = abs(p0[i] - p1[i])
        if d:
            diff_bytes += 1
            if d > max_err:
                max_err = d
    # Differing pixels ≈ differing-byte triples; report a conservative upper bound.
    diff_pixels = diff_bytes  # per-channel byte count (coarse but monotone)
    total = w0 * h0 * 3
    identical = max_err == 0
    status = "IDENTICAL" if identical else f"max_err={max_err} diff_channels={diff_bytes}/{total} ({100.0*diff_bytes/total:.3f}%)"
    print(f"bmpdiff {args[0]} vs {args[1]}: {status}")
    if max_err <= tol and diff_pixels <= maxpix:
        return 0
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
