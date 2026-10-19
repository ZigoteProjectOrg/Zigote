// height: 300
class Demo : ComposedWidget
{
    // The empty state: an icon, a title and a description, with optional content below.
    protected override Widget Build(BuildContext context) =>
        new AdwStatusPage {
            IconName = MaterialIcons.Inbox,
            Title = "No Messages",
            Description = "New messages from your contacts will appear here",
        };
}
