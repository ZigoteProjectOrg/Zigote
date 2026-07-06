namespace Zigote.World;

/// <summary>
///     Bidirectional tag ↔ entity-id index for gameplay queries ("all Enemies"). Tags are free-form
///     ordinal strings; an entity has at most one tag (null/empty = untagged). Per-tag id lists are
///     retained across retags so steady-state churn allocates nothing.
/// </summary>
public sealed class TagIndex
{
    private readonly Dictionary<string, List<int>> _byTag = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _tagOf = new();

    public void Set(int id, string? tag)
    {
        if (_tagOf.TryGetValue(id, out var old))
        {
            if (old == tag) return;
            _byTag[old].Remove(id);
            _tagOf.Remove(id);
        }

        if (string.IsNullOrEmpty(tag)) return;
        if (!_byTag.TryGetValue(tag, out var list)) _byTag[tag] = list = [];
        list.Add(id);
        _tagOf[id] = tag;
    }

    public void Remove(int id)
    {
        Set(id, null);
    }

    public string? TagOf(int id)
    {
        return _tagOf.GetValueOrDefault(id);
    }

    /// <summary>Ids carrying <paramref name="tag" />, appended to <paramref name="results" /> after clearing it.</summary>
    public int WithTag(string tag, List<int> results)
    {
        results.Clear();
        if (_byTag.TryGetValue(tag, out var list)) results.AddRange(list);
        return results.Count;
    }

    public int Count(string tag)
    {
        return _byTag.TryGetValue(tag, out var list) ? list.Count : 0;
    }

    public void Clear()
    {
        _byTag.Clear();
        _tagOf.Clear();
    }
}