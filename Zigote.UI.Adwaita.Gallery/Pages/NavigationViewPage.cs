namespace AdwaitaGallery.Pages;

/// <summary>
///     Navigation View — AdwNavigationView driving a page stack, both inline (the stage below) and
///     as the whole content of a dialog.
/// </summary>
public sealed class NavigationViewPage : StatelessWidget
{
    protected override Widget Build(BuildContext context)
    {
        return new GalleryPage(
            "Navigation View",
            "A stack of pages that push and pop, with the header following the top one.",
            MaterialIcons.Layers
        ) {
            ClampWidth = 680f,
            Children = {
                Demo.Titled(
                    "Inline",
                    "The same container, sized into the page instead of a window.",
                    new SizedBox(height: 300f, child: Inline())
                ),
                Demo.Group(
                    "In a Dialog",
                    "How a GNOME app runs a multi-step flow without leaving the window.",
                    new AdwActionRow("Open the Demo", "Four pages on one stack") {
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
                new AdwToolbarView(new Center { Child = column }) { TopBars = { bar } },
                title
            );
        }

        AdwNavigationPage Details()
        {
            return Page("Details", "Pop with the back button or a system back gesture.", false);
        }

        nav = new AdwNavigationView(
            Page(
                "Library",
                "The root page of the stack.",
                true,
                new AdwButton("Open Details", () => nav.Push(Details())) { Pill = true }
            )
        ) { AutoHeaderBar = false };

        return new ClipRRect(AdwMetrics.CardRadius, nav);
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
            bar.End.Add(Demo.IconButton(MaterialIcons.Close, () => dialog?.Close()));
            return new AdwNavigationPage(
                new AdwToolbarView(new Center { Child = content }) { TopBars = { bar } },
                title
            );
        }

        AdwNavigationPage Page3()
        {
            return Page(
                "Page 3",
                false,
                new Label("Page 3", AdwTypography.Title1) {
                    MaxLines = 1,
                    Overflow = TextOverflow.Ellipsis,
                }
            );
        }

        AdwNavigationPage Page4()
        {
            return Page(
                "Page 4",
                false,
                new AdwButton("Open Page 3", () => nav.Push(Page3())) { Pill = true }
            );
        }

        AdwNavigationPage Page2()
        {
            return Page(
                "Page 2",
                false,
                new AdwButton("Open Page 4", () => nav.Push(Page4())) { Pill = true }
            );
        }

        nav = new AdwNavigationView(
            Page(
                "Page 1",
                true,
                new Column(spacing: Spacing.Lg, mainAxisSize: MainAxisSize.Min) {
                    Children = {
                        new AdwButton("Open Page 2", () => nav.Push(Page2())) { Pill = true },
                        new AdwButton("Open Page 3", () => nav.Push(Page3())) { Pill = true },
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