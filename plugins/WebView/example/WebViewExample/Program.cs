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
            // The one thing worth configuring, and only before the App exists: give the webview
            // its own GTK thread, so a scrolling page costs the UI thread nothing. (To force the
            // X11 overlay instead — a GPU-composited page, at the cost of running the whole app
            // through XWayland — call EnsureEmbeddableVideoDriver() here as well.)
            WebView.WebViewController.EnsureThreadedWebView();
            new BrowserApp().Run();
        }
        finally
        {
            AppLog.Shutdown();
        }
    }
}
