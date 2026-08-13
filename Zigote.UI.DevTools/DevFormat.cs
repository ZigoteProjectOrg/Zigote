using System.Globalization;

namespace Zigote.UI.DevTools;

/// <summary>
///     Compact, allocation-light formatting helpers for diagnostics readouts (counts, byte sizes,
///     durations). Invariant culture throughout so a devtools panel reads identically on every machine.
/// </summary>
public static class DevFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Abbreviate a large count: 1234 → "1.23K", 4_500_000 → "4.50M".</summary>
    public static string Count(long n)
    {
        var a = Math.Abs(n);
        return a switch {
            >= 1_000_000_000 => (n / 1e9).ToString("0.##", Inv) + "B",
            >= 1_000_000 => (n / 1e6).ToString("0.##", Inv) + "M",
            >= 10_000 => (n / 1e3).ToString("0.#", Inv) + "K",
            _ => n.ToString(Inv),
        };
    }

    public static string Count(ulong n)
    {
        return Count((long)n);
    }

    /// <summary>Human byte size: 1536 → "1.50 KB", 5 MiB → "5.00 MB".</summary>
    public static string Bytes(ulong bytes)
    {
        double b = bytes;
        return bytes switch {
            >= 1UL << 30 => (b / (1 << 30)).ToString("0.0", Inv) + " GB",
            >= 1UL << 20 => (b / (1 << 20)).ToString("0.0", Inv) + " MB",
            >= 1UL << 10 => (b / (1 << 10)).ToString("0.0", Inv) + " KB",
            _ => bytes + " B",
        };
    }

    public static string Bytes(long bytes)
    {
        return bytes < 0 ? "—" : Bytes((ulong)bytes);
    }

    /// <summary>Megabytes with one decimal, e.g. "142.3 MB". Input is already in MB.</summary>
    public static string Mb(float mb)
    {
        return mb.ToString("0.0", Inv) + " MB";
    }

    /// <summary>Session uptime: 3725 s → "1h 2m 5s".</summary>
    public static string Uptime(float seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0f, seconds));
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m {t.Seconds}s";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds}s";
        return $"{t.Seconds}s";
    }

    public static string Ms(double ms)
    {
        return ms.ToString("0.00", Inv) + " ms";
    }
}
