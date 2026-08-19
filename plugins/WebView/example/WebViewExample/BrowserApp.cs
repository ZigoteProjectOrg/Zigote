using Serilog;
using Zigote.Core;
using Zigote.UI.Adwaita;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace WebViewExample;

/// <summary>
///     A minimal browser: back/forward, a URL bar, a progress bar, a title readout, and the page —
///     every part of the WebView plugin's API on one screen. Type <c>demo</c> in the URL bar to
///     load the local page that talks to this host over the message bridge.
/// </summary>
public sealed class BrowserApp : AdwaitaApp
{
    public BrowserApp() : base(home: new SafeArea(new BrowserPage()), title: "WebView example")
    {
        Width = 1000;
        Height = 720;
    }
}

internal sealed class BrowserPage : ComposedWidget
{
    private static readonly ILogger Log = Serilog.Log.ForContext<BrowserPage>();

    /// <summary>The bridge, both directions, in one page: it announces itself on load, answers
    ///     whatever the host sends, and can be poked by hand.</summary>
    private const string BridgeDemo =
        """
        <body style="font:16px system-ui;margin:2rem;background:#1c1c1e;color:#eee">
          <h1>Bridge demo</h1>
          <button onclick="window.zigote.postMessage({ kind: 'click', at: Date.now() })">
            send a message to the host
          </button>
          <p>host says: <b id="from-host">nothing yet</b></p>
          <p>user script left behind: <b id="seeded"></b></p>
          <script>
            document.getElementById('seeded').textContent = window.DEMO_SECRET ?? '(nothing)';
            window.zigote.onMessage(m => {
              document.getElementById('from-host').textContent = JSON.stringify(m);
            });
            window.zigote.postMessage({ kind: 'ready' });
          </script>
        </body>
        """;

    private readonly global::WebView.WebViewController _controller = new(
        new global::WebView.WebViewSettings { DevToolsEnabled = true });

    private readonly AdwEntry _url = new();
    private readonly AdwWindowTitle _title = new("WebView example");
    private readonly AdwProgressBar _progress = new();
    private readonly AdwButton _back;
    private readonly AdwButton _forward;

    public BrowserPage()
    {
        _back = new AdwButton(onPressed: _controller.GoBack) { IconName = MaterialIcons.ArrowBack };
        _forward = new AdwButton(onPressed: _controller.GoForward) { IconName = MaterialIcons.ArrowForward };
    }

    protected override void OnMount()
    {
        // Before the first mount, so it is in place for the very first document.
        _controller.AddUserScript("window.DEMO_SECRET = 'injected at document-start';");

        _controller.UrlChanged += url => _url.Text = url;
        _controller.TitleChanged += title => _title.Title = title;
        _controller.ProgressChanged += p => _progress.Value = (float)p;
        _controller.HistoryChanged += () =>
        {
            _back.Enabled = _controller.CanGoBack;
            _forward.Enabled = _controller.CanGoForward;
        };
        _controller.LoadFailed += error => _title.Title = error.Message;
        _controller.MessageReceived += message =>
        {
            Log.Information("Page says: {Message}", message);
            // Answer it, so the round trip is visible on the page itself.
            _ = _controller.PostMessageAsync(new { kind = "ack", received = message });
        };
        _url.OnSubmitted = Go;

        Go("demo");

        // A backend that never attaches fires no load events, so surface the reason directly —
        // posted, because the WebView child attaches during this same mount pass.
        Zigote.UI.Host.App.Active?.Post(() =>
        {
            if (_controller.LastError is { } error)
            {
                _title.Title = error;
                Log.Error("No webview: {Reason}", error);
            }
        });
    }

    protected override void OnUnmount() => _controller.Dispose();

    private void Go(string text)
    {
        if (text is "demo")
        {
            _url.Text = "demo";
            _controller.LoadHtml(BridgeDemo);
            return;
        }

        string url = text.Contains("://") ? text : "https://" + text;
        _url.Text = url;
        _controller.Navigate(url);
    }

    protected override Widget Build(BuildContext context)
    {
        return new AdwToolbarView(
            new global::WebView.WebView(_controller)
        ) {
            TopBars = {
                new AdwHeaderBar {
                    TitleWidget = _title,
                    Start = {
                        _back,
                        _forward,
                        new AdwButton(onPressed: _controller.Reload) { IconName = MaterialIcons.Refresh },
                    },
                },
                new Padding(
                    padding: EdgeInsets.All(8),
                    child: _url
                ),
                _progress,
            },
        };
    }
}
