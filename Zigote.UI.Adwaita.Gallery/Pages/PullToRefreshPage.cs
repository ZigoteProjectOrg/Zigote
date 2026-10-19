namespace AdwaitaGallery.Pages;

/// <summary>
///     Pull to Refresh — the whole page is the demo: pull past its top with a trackpad or a finger
///     and a fake fetch lands one more message in the inbox.
/// </summary>
public sealed class PullToRefreshPage : ComposedWidget
{
    private readonly Signal<int> _fetched = new(3);

    protected override Widget Build(BuildContext context)
    {
        return new AdwPullToRefresh(
            child: new GalleryPage(
                title: "Pull to Refresh",
                description:
                "Pull past the top with two fingers or a finger, and let go once the spinner is full.",
                iconName: MaterialIcons.Refresh
            ) {
                Children = { new Watch(Inbox) },
            },
            onRefresh: Fetch
        );
    }

    private async Task Fetch()
    {
        await Task.Delay(1200);
        // No UI synchronization context: the continuation is on the thread pool, so the write is
        // posted back. It lands before the spinner's own completion post, so the row is in first.
        App.Active?.Post(() => _fetched.Value++);
    }

    private Widget Inbox()
    {
        int n = _fetched.Value;
        var group = Demo.Group(title: "Inbox", description: $"{n} messages, newest first");
        for (int i = n; i >= 1; i--)
            group.Rows.Add(new AdwActionRow(title: $"Message {i}", subtitle: i == n ? "Just now" : "Earlier"));
        return group;
    }
}
