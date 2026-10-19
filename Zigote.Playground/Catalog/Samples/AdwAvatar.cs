// height: 180
class Demo : ComposedWidget
{
    // Initials on a colour derived from the text, at any size; or an icon.
    protected override Widget Build(BuildContext context) =>
        new Center(new Row(mainAxisSize: MainAxisSize.Min, spacing: 16, children: [
            new AdwAvatar(size: 24, text: "Ada Lovelace"),
            new AdwAvatar(size: 40, text: "Grace Hopper"),
            new AdwAvatar(size: 64, text: "Alan Turing"),
            new AdwAvatar(size: 96, text: "Katherine Johnson"),
            new AdwAvatar(size: 64, iconName: MaterialIcons.Person),
        ]));
}
