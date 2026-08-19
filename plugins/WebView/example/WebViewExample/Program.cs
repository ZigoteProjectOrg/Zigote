using Serilog.Events;
using Zigote.Logging;

namespace WebViewExample;

public static class Program
{
    public static void Main()
    {
        // Debug on the console: the plugin's embed, load, navigation-block and page-message
        // breadcrumbs all go through Zigote.Logging, and this is what makes them visible.
        AppLog.Bootstrap(LogEventLevel.Debug);
        AppLog.CaptureFailures();
        try
        {
            // Nothing to configure: native Wayland renders the page into an engine texture, X11
            // gets a true overlay. (To force the overlay on a Wayland desktop, call
            // WebViewController.EnsureEmbeddableVideoDriver() here, before the App exists.)
            new BrowserApp().Run();
        }
        finally
        {
            AppLog.Shutdown();
        }
    }
}
