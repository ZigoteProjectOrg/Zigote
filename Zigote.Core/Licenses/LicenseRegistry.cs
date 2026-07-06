using System.Text;

namespace Zigote.Core.Licenses;

/// <summary>
///     One attribution on an app's licenses screen: the component, the license it ships under, and
///     the license / attribution text itself.
/// </summary>
public sealed record LicenseEntry(string Component, string License, string Text)
{
    public string? Homepage { get; init; }
}

/// <summary>
///     The process-wide registry of open-source attributions.
///     Zigote registers its own bundled components (the native engine's dependencies here in Core;
///     the bundled fonts from Zigote.UI) and an app adds its own via <see cref="Add" /> or a lazy
///     <see cref="AddCollector" />. <see cref="BuildText" /> renders everything as one plain-text
///     document ready to show in an about screen, write to disk, or print to a console; the
///     <c>LicensesView</c> widget in Zigote.UI displays it in-app.
/// </summary>
public static class LicenseRegistry
{
    private const string Separator =
        "------------------------------------------------------------------------";

    private static readonly object Gate = new();
    private static readonly List<LicenseEntry> Entries = [];
    private static readonly List<Func<IEnumerable<LicenseEntry>>> Collectors = [];

    static LicenseRegistry()
    {
        Collectors.Add(ZigoteLicenses.Create);
    }

    public static void Add(LicenseEntry entry)
    {
        lock (Gate)
        {
            Entries.Add(entry);
        }
    }

    /// <summary>Register entries lazily — the collector runs once, on the first enumeration.</summary>
    public static void AddCollector(Func<IEnumerable<LicenseEntry>> collector)
    {
        lock (Gate)
        {
            Collectors.Add(collector);
        }
    }

    /// <summary>Resolve pending collectors and return a snapshot in registration order.</summary>
    public static IReadOnlyList<LicenseEntry> Collect()
    {
        lock (Gate)
        {
            if (Collectors.Count > 0)
            {
                // Drain into a local first: a collector that registers further entries/collectors
                // would otherwise mutate the lists mid-iteration.
                var pending = Collectors.ToArray();
                Collectors.Clear();
                foreach (var collector in pending)
                    Entries.AddRange(collector());
            }

            return Entries.ToArray();
        }
    }

    /// <summary>Render every registered attribution as a single plain-text document.</summary>
    public static string BuildText(string? title = null)
    {
        var entries = Collect();
        var sb = new StringBuilder(entries.Count * 1024);
        if (!string.IsNullOrEmpty(title))
        {
            sb.AppendLine(title);
            sb.AppendLine();
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (i > 0) sb.AppendLine();
            sb.Append(e.Component).Append(" — ").AppendLine(e.License);
            if (e.Homepage is { Length: > 0 } url) sb.AppendLine(url);
            sb.AppendLine(Separator);
            sb.AppendLine(e.Text.Trim());
        }

        return sb.ToString();
    }
}