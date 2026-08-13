namespace AdwaitaGallery.Pages;

/// <summary>
///     Toasts — transient messages, raised through the window's own toast host rather than a
///     page-owned overlay, so they behave the same wherever they come from.
/// </summary>
public sealed class ToastsPage : ComposedWidget
{
    private const string LongTitle =
        "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor " +
        "incididunt ut labore et dolore magnam aliquam quaerat voluptatem.";

    private int _deleted;

    protected override Widget Build(BuildContext context)
    {
        var host = GalleryHost.Of(context);

        return new GalleryPage(
            title: "Toasts",
            description:
            "A message that says what happened, optionally with the one action that undoes it.",
            iconName: MaterialIcons.Notifications
        ) {
            Children = {
                Demo.Group(
                    title: "Show a Toast",
                    description: "They queue: raise several and they take their turn.",
                    Row(
                        title: "Simple",
                        subtitle: "Just a message",
                        show: () => host.Toast("File saved")
                    ),
                    Row(
                        title: "With an action",
                        subtitle: "The Undo pattern",
                        show: () => Undo(host)
                    ),
                    Row(
                        title: "Long title",
                        subtitle: "Wraps to two lines and stays readable",
                        show: () => host.Toast(LongTitle)
                    ),
                    Row(
                        title: "Three at once",
                        subtitle: "The overlay stacks them",
                        show: () =>
                        {
                            host.Toast("Copied to clipboard");
                            host.Toast("Upload finished");
                            host.Toast("Signed in as ada@example.org");
                        }
                    )
                ),
                Demo.Group(
                    title: "Where They Belong",
                    description: null,
                    new AdwActionRow(
                        title: "One host per window",
                        subtitle:
                        "The shell owns it, so a toast from a dialog lands in the same place"
                    ) { IconName = MaterialIcons.Notifications },
                    new AdwActionRow(
                        title: "Never for errors that need a decision",
                        subtitle: "That is an alert dialog"
                    ) {
                        IconName = MaterialIcons.WebAsset,
                        ShowChevron = true,
                        OnActivated = () => host.Open("Alert Dialogs"),
                    }
                ),
            },
        };
    }

    private static Widget Row(string title, string subtitle, Action show)
    {
        return new AdwActionRow(title: title, subtitle: subtitle) {
            Suffixes = { new AdwButton(label: "Show", onPressed: show) },
        };
    }

    /// <summary>The GNOME undo toast: one message that counts up as more is deleted.</summary>
    private void Undo(GalleryHost host)
    {
        _deleted++;
        string title = _deleted == 1 ? "‘Lorem Ipsum’ deleted" : $"{_deleted} items deleted";
        host.Toast(
            title: title,
            buttonLabel: "Undo",
            onButtonClicked: () =>
            {
                int items = _deleted;
                _deleted = 0;
                host.Toast(items == 1 ? "Restored 1 item" : $"Restored {items} items");
            }
        );
    }
}
