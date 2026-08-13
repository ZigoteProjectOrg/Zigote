using Zigote.Preferences;

namespace Zigote.Editor.Settings;

/// <summary>
///     Project history as preferences: the recent-project list (File ▸ Open Recent, welcome
///     screen) and the last opened project. Lives in the same SQLite-backed store as
///     <see cref="EditorSettings" /> but in its own "projects" group — the Settings window's
///     "Reset All" resets the "editor" group only, so history survives it. The list is replaced
///     wholesale on every change (preference values are immutable); the structural comparer keeps
///     a no-op replacement from persisting or notifying.
/// </summary>
public sealed class ProjectHistory : PreferencesProvider
{
    private const int MaxRecent = 12;

    public ProjectHistory(PreferenceStore store) : base(store: store, prefix: "projects")
    {
        Recent = Register(
            key: "recent",
            defaultValue: Array.Empty<string>(),
            comparer: OrdinalArrayComparer.Instance
        );
        Last = Register<string?>(key: "last", defaultValue: null);
    }

    /// <summary>Most-recent-first absolute project paths, capped at 12.</summary>
    public Preference<string[]> Recent { get; }

    /// <summary>The last opened project (reopened on launch when the preference allows).</summary>
    public Preference<string?> Last { get; }

    /// <summary>Record a project as the most-recently opened.</summary>
    public void RecordOpened(string projectPath)
    {
        string full = Path.GetFullPath(projectPath);
        Recent.Update(list => PrependDistinct(list: list, full: full));
        Last.Value = full;
    }

    /// <summary>Drop a project from the recent list (e.g. when it no longer exists).</summary>
    public void Forget(string projectPath)
    {
        string full = Path.GetFullPath(projectPath);
        Recent.Update(list => list.Where(p => !PathEquals(a: p, b: full)).ToArray());
        if (Last.Peek() is { } last && PathEquals(a: last, b: full)) Last.Value = null;
    }

    /// <summary>Empty the recent list (the Settings window's Clear button); Last is kept.</summary>
    public void ClearRecent() => Recent.Reset();

    private static string[] PrependDistinct(string[] list, string full)
    {
        var result = new List<string>(list.Length + 1) { full };
        result.AddRange(list.Where(p => !PathEquals(a: p, b: full)));
        if (result.Count > MaxRecent)
            result.RemoveRange(index: MaxRecent, count: result.Count - MaxRecent);
        return result.ToArray();
    }

    private static bool PathEquals(string a, string b)
    {
        try
        {
            return string.Equals(
                a: Path.GetFullPath(a),
                b: Path.GetFullPath(b),
                comparisonType: StringComparison.OrdinalIgnoreCase
            );
        }
        catch
        {
            return string.Equals(a: a, b: b, comparisonType: StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class OrdinalArrayComparer : IEqualityComparer<string[]>
    {
        public static readonly OrdinalArrayComparer Instance = new();

        public bool Equals(string[]? x, string[]? y)
        {
            if (ReferenceEquals(objA: x, objB: y)) return true;
            if (x is null || y is null) return false;
            return x.AsSpan().SequenceEqual(y);
        }

        public int GetHashCode(string[] obj)
        {
            var hash = new HashCode();
            foreach (string s in obj) hash.Add(value: s, comparer: StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }
}
