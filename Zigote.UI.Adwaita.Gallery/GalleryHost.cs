namespace AdwaitaGallery;

/// <summary>
///     The ambient services of one gallery window, reached from any page with
///     <see cref="Of" />: the window's single toast host, the app-wide appearance state, and
///     navigation to another page. Pages therefore carry no overlay of their own — toasts from
///     anywhere in a window stack in the same place, and a second window gets its own host.
///     <para>
///         Dialog content is pushed onto the App overlay stack, which is NOT below this widget:
///         capture the host in the page's Build and close over it instead of calling
///         <see cref="Of" /> from inside a dialog.
///     </para>
/// </summary>
internal sealed class GalleryHost : InheritedWidget
{
    private readonly Shell _shell;

    public GalleryHost(GalleryApp app, Shell shell, Widget child)
    {
        App = app;
        _shell = shell;
        Child = child;
    }

    public GalleryApp App { get; }

    public static GalleryHost Of(BuildContext ctx)
    {
        return ctx.DependOn<GalleryHost>() ??
               throw new InvalidOperationException("No GalleryHost above this widget.");
    }

    /// <summary>Float a toast over this window.</summary>
    public void Toast(string title, string? buttonLabel = null, Action? onButtonClicked = null)
    {
        _shell.Toast(
            new AdwToast(title) {
                ButtonLabel = buttonLabel,
                OnButtonClicked = onButtonClicked,
            }
        );
    }

    /// <summary>Open the page with this title (the sidebar follows), for in-page cross links.</summary>
    public void Open(string pageTitle) => _shell.Open(pageTitle);

    /// <summary>Open the preferences dialog — the Ctrl+, shortcut and a page's cross link.</summary>
    public void ShowPreferences() => _shell.ShowPreferences();

    // The data never changes — the shell instance is fixed for the life of the window.
    public override bool UpdateShouldNotify(InheritedWidget oldWidget) => false;
}
