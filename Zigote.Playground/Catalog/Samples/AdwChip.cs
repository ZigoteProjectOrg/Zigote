// height: 180
class Demo : ComposedWidget
{
    private static readonly string[] Filters = ["Unread", "Starred", "Attachments", "From me", "This week"];
    private readonly Signal<HashSet<string>> _on = new([], new SetComparer());

    private sealed class SetComparer : IEqualityComparer<HashSet<string>>
    {
        public bool Equals(HashSet<string>? a, HashSet<string>? b) => a!.SetEquals(b!);
        public int GetHashCode(HashSet<string> s) => s.Count;
    }

    // Toggleable filter chips: a new set on every change, so the signal sees it.
    protected override Widget Build(BuildContext context) =>
        new Center(new Watch(() => new Wrap(spacing: 8, runSpacing: 8, children: [
            .. Filters.Select(f => new AdwChip(f, selected: _on.Value.Contains(f), onPressed: () =>
            {
                var next = new HashSet<string>(_on.Value);
                if (!next.Add(f)) next.Remove(f);
                _on.Value = next;
            })),
        ])));
}
