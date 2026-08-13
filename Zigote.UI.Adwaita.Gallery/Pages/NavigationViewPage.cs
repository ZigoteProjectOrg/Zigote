namespace AdwaitaGallery.Pages;

/// <summary>
///     Navigation View — AdwNavigationView driving a page stack, both inline (the stage below) and
///     as the whole content of a dialog.
/// </summary>
public sealed class NavigationViewPage : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        return new GalleryPage(
            title: "Navigation View",
            description:
            "A stack of pages that push and pop, with the header following the top one.",
            iconName: MaterialIcons.Layers
        ) {
            ClampWidth = 680f,
            Children = {
                Demo.Titled(
                    title: "Inline",
                    description: "The same container, sized into the page instead of a window.",
                    child: new SizedBox(height: 300f, child: Inline())
                ),
                Demo.Group(
                    title: "In a Dialog",
                    description:
                    "How a GNOME app runs a multi-step flow without leaving the window.",
                    new AdwActionRow(title: "Open the Demo", subtitle: "Four pages on one stack") {
                        ShowChevron = true,
                        OnActivated = RunDemo,
                    }
                ),
            },
        };
    }

    /// <summary>A stack sized into the page: push a couple of pages, pop with the back button.</summary>
    private static Widget Inline()
    {
        AdwNavigationView nav = null!;

        AdwNavigationPage Page(string title, string body, bool root, Widget? action = null)
        {
            var bar = new AdwHeaderBar {
                Title = title,
                Flat = true,
                ShowBackButton = !root,
                OnBack = () => nav.Pop(),
                ShowStartWindowControls = false,
                ShowEndWindowControls = false,
            };
            var column = new Column(spacing: Spacing.Md, mainAxisSize: MainAxisSize.Min) {
                Children = { Demo.Caption(body) },
            };
            if (action is not null) column.Children.Add(action);
            return new AdwNavigationPage(
                child: new AdwToolbarView(new Center { Child = column }) { TopBars = { bar } },
                title: title
            );
        }

        AdwNavigationPage Details() => Page(
            title: "Details",
            body: "Pop with the back button or a system back gesture.",
            root: false
        );

        nav = new AdwNavigationView(
            Page(
                title: "Library",
                body: "The root page of the stack.",
                root: true,
                action: new AdwButton(label: "Open Details", onPressed: () => nav.Push(Details())) {
                    Pill = true,
                }
            )
        ) { AutoHeaderBar = false };

        return new ClipRRect(radius: AdwMetrics.CardRadius, child: nav);
    }

    /// <summary>The dialog flow: four pages pushed onto one stack, each with a close button.</summary>
    private static void RunDemo()
    {
        AdwDialog? dialog = null;
        AdwNavigationView nav = null!;

        AdwNavigationPage Page(string title, bool root, Widget content)
        {
            var bar = new AdwHeaderBar {
                Title = title,
                ShowBackButton = !root,
                OnBack = () => nav.Pop(),
                ShowStartWindowControls = false,
                ShowEndWindowControls = false,
            };
            bar.End.Add(
                Demo.IconButton(icon: MaterialIcons.Close, onPressed: () => dialog?.Close())
            );
            return new AdwNavigationPage(
                child: new AdwToolbarView(new Center { Child = content }) { TopBars = { bar } },
                title: title
            );
        }

        AdwNavigationPage Page3()
        {
            return Page(
                title: "Page 3",
                root: false,
                content: new Label(text: "Page 3", style: AdwTypography.Title1) {
                    MaxLines = 1,
                    Overflow = TextOverflow.Ellipsis,
                }
            );
        }

        AdwNavigationPage Page4()
        {
            return Page(
                title: "Page 4",
                root: false,
                content: new AdwButton(label: "Open Page 3", onPressed: () => nav.Push(Page3())) {
                    Pill = true,
                }
            );
        }

        AdwNavigationPage Page2()
        {
            return Page(
                title: "Page 2",
                root: false,
                content: new AdwButton(label: "Open Page 4", onPressed: () => nav.Push(Page4())) {
                    Pill = true,
                }
            );
        }

        nav = new AdwNavigationView(
            Page(
                title: "Page 1",
                root: true,
                content: new Column(spacing: Spacing.Lg, mainAxisSize: MainAxisSize.Min) {
                    Children = {
                        new AdwButton(label: "Open Page 2", onPressed: () => nav.Push(Page2())) {
                            Pill = true,
                        },
                        new AdwButton(label: "Open Page 3", onPressed: () => nav.Push(Page3())) {
                            Pill = true,
                        },
                    },
                }
            )
        ) { AutoHeaderBar = false };

        dialog = new AdwDialog(nav) {
            ContentWidth = 380f,
            ContentHeight = 380f,
        };
        dialog.Show();
    }
}
