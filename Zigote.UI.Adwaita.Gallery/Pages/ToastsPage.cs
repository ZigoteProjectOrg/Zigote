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
            "Toasts",
            "A message that says what happened, optionally with the one action that undoes it.",
            MaterialIcons.Notifications
        ) {
            Children = {
                Demo.Group(
                    "Show a Toast",
                    "They queue: raise several and they take their turn.",
                    Row("Simple", "Just a message", () => host.Toast("File saved")),
                    Row("With an action", "The Undo pattern", () => Undo(host)),
                    Row(
                        "Long title",
                        "Wraps to two lines and stays readable",
                        () => host.Toast(LongTitle)
                    ),
                    Row(
                        "Three at once",
                        "The overlay stacks them",
                        () =>
                        {
                            host.Toast("Copied to clipboard");
                            host.Toast("Upload finished");
                            host.Toast("Signed in as ada@example.org");
                        }
                    )
                ),
                Demo.Group(
                    "Where They Belong",
                    null,
                    new AdwActionRow(
                        "One host per window",
                        "The shell owns it, so a toast from a dialog lands in the same place"
                    ) { IconName = MaterialIcons.Notifications },
                    new AdwActionRow(
                        "Never for errors that need a decision",
                        "That is an alert dialog"
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
        return new AdwActionRow(title, subtitle) {
            Suffixes = { new AdwButton("Show", show) },
        };
    }

    /// <summary>The GNOME undo toast: one message that counts up as more is deleted.</summary>
    private void Undo(GalleryHost host)
    {
        _deleted++;
        var title = _deleted == 1 ? "‘Lorem Ipsum’ deleted" : $"{_deleted} items deleted";
        host.Toast(
            title,
            "Undo",
            () =>
            {
                var items = _deleted;
                _deleted = 0;
                host.Toast(items == 1 ? "Restored 1 item" : $"Restored {items} items");
            }
        );
    }
}