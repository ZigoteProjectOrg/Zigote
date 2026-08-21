using AppLinks;
using AppSettings;
using Battery;
using Biometrics;
using Connectivity;
using DeviceInfo;
using FilePicker;
using Geolocation;
using Haptics;
using Notifications;
using PathProvider;
using Permissions;
using Push;
using SecureStorage;
using Sensors;
using Share;
using UrlLauncher;
using Zigote.UI.Adwaita;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace PluginsDemo;

/// <summary>Page furniture shared by every plugin page — a preferences page, rows, a result line.</summary>
internal static class Demo
{
    /// <summary>The page shell: groups down a scrolling preferences page.</summary>
    public static Widget Page(params AdwPreferencesGroup[] groups)
    {
        var page = new AdwPreferencesPage();
        foreach (var group in groups) page.Groups.Add(group);
        return page;
    }

    /// <summary>A read-only fact: title on the left, value on the right, em dash for nothing.</summary>
    public static AdwActionRow Fact(string title, string? value, string? subtitle = null)
        => new(title, subtitle) { Suffixes = { new Text(string.IsNullOrEmpty(value) ? "—" : value) } };

    /// <summary>A row that does something.</summary>
    public static AdwActionRow Action(string title, string subtitle, string button, Action onPressed)
        => new(title, subtitle) { Suffixes = { new AdwButton(button, onPressed) } };

    /// <summary>The line every page prints its last answer on.</summary>
    public static Widget Result(Signal<string> text)
        => new Watch(() => new Padding(
            EdgeInsets.All(12),
            new Label(text.Value)));
}

// ── Device ───────────────────────────────────────────────────────────────────

/// <summary>DeviceInfo and PathProvider: the two plugins that are pure facts.</summary>
internal sealed class DevicePage : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        var device = DeviceInfoPlugin.Get();
        return Demo.Page(
            new AdwPreferencesGroup("Device", "DeviceInfo — one cached snapshot, no channels")
            {
                Rows =
                {
                    Demo.Fact("Operating system", device.Os),
                    Demo.Fact("Model", device.Model),
                    Demo.Fact("Manufacturer", device.Manufacturer),
                    Demo.Fact("Architecture", device.Architecture),
                    Demo.Fact("Device ID", device.DeviceId, "Diagnostics, not tracking"),
                },
            },
            new AdwPreferencesGroup("Application")
            {
                Rows =
                {
                    Demo.Fact("Name", device.AppName),
                    Demo.Fact("Version", device.AppVersion),
                    Demo.Fact("Package", device.PackageId),
                },
            },
            new AdwPreferencesGroup("Paths", "PathProvider — the folders this app owns")
            {
                Rows =
                {
                    Demo.Fact("Data", PathProviderPlugin.Data()),
                    Demo.Fact("Config", PathProviderPlugin.Config()),
                    Demo.Fact("Cache", PathProviderPlugin.Cache()),
                    Demo.Fact("Documents", PathProviderPlugin.Documents()),
                    Demo.Fact("Downloads", PathProviderPlugin.Downloads()),
                    Demo.Fact("Temp", PathProviderPlugin.Temp()),
                },
            });
    }
}

// ── Battery ──────────────────────────────────────────────────────────────────

/// <summary>Battery: a snapshot, re-read on demand — the plugin has no stream by design.</summary>
internal sealed class BatteryPage : ComposedWidget
{
    private readonly Signal<BatteryReading> _reading = new(BatteryPlugin.Read());

    protected override Widget Build(BuildContext context) => Demo.Page(
        new AdwPreferencesGroup("Battery", "battery_plus — poll it; the level moves in minutes")
        {
            Rows =
            {
                new Watch(() => Demo.Fact(
                    "Charge",
                    _reading.Value.Percent < 0 ? null : _reading.Value.Percent + "%")),
                new Watch(() => Demo.Fact("Status", _reading.Value.Status.ToString())),
                new Watch(() => Demo.Fact("Power saver", _reading.Value.SaverOn ? "On" : "Off")),
                new Watch(() => Demo.Fact("Battery present", _reading.Value.Present ? "Yes" : "No")),
                Demo.Action("Read again", "BatteryPlugin.Read()", "Refresh",
                    () => _reading.Value = BatteryPlugin.Read()),
            },
        });
}

// ── Network ──────────────────────────────────────────────────────────────────

/// <summary>Connectivity: the current link, plus live changes — unplug the cable and watch.</summary>
internal sealed class NetworkPage : ComposedWidget
{
    private readonly Signal<string> _log = new("Listening for changes…");
    private readonly Signal<Connection> _now = new(ConnectivityPlugin.Current);
    private IDisposable? _subscription;

    protected override void OnMount()
        => _subscription = ConnectivityPlugin.Listen(connection =>
        {
            // Watch marshals an off-thread signal write onto the UI thread itself.
            _now.Value = connection;
            _log.Value = $"Changed to {connection.Kind} at {DateTime.Now:HH:mm:ss}";
        });

    public override void Detach()
    {
        _subscription?.Dispose();
        _subscription = null;
        base.Detach();
    }

    protected override Widget Build(BuildContext context) => Demo.Page(
        new AdwPreferencesGroup("Connection", "connectivity_plus — a route out, not a reachable internet")
        {
            Rows =
            {
                new Watch(() => Demo.Fact("Online", _now.Value.Online ? "Yes" : "No")),
                new Watch(() => Demo.Fact("Kind", _now.Value.Kind.ToString())),
                new Watch(() => Demo.Fact("Metered", _now.Value.IsCellular ? "Cellular — ask before big downloads" : "No")),
                Demo.Action("Read again", "ConnectivityPlugin.Current", "Refresh",
                    () => _now.Value = ConnectivityPlugin.Current),
            },
        },
        new AdwPreferencesGroup("Events") { Rows = { new Watch(() => Demo.Fact("Last change", _log.Value)) } });
}

// ── Secrets ──────────────────────────────────────────────────────────────────

/// <summary>SecureStorage: write, read back, delete — against the real OS keystore.</summary>
internal sealed class SecretsPage : ComposedWidget
{
    private readonly AdwEntryRow _key = new("Key", "demo.token");
    private readonly Signal<string> _status = new("Nothing done yet");
    private readonly AdwEntryRow _value = new("Value", "hunter2");

    protected override Widget Build(BuildContext context) => Demo.Page(
        new AdwPreferencesGroup(
            "Secure storage",
            SecureStoragePlugin.Available
                ? $"Keystore available — filed under \"{SecureStoragePlugin.Service}\""
                : "No keystore on this machine: writes fail and reads are null, by design")
        {
            Rows =
            {
                _key,
                _value,
                Demo.Action("Write", "WriteAsync(key, value)", "Save", () => Run(async () =>
                    await SecureStoragePlugin.WriteAsync(_key.Text, _value.Text)
                        ? "Stored"
                        : "Refused — no keystore")),
                Demo.Action("Read", "ReadAsync(key)", "Load", () => Run(async () =>
                    await SecureStoragePlugin.ReadAsync(_key.Text) is { } secret
                        ? $"Read back: {secret}"
                        : "Nothing stored under that key")),
                Demo.Action("Delete", "DeleteAsync(key)", "Forget", () => Run(async () =>
                    await SecureStoragePlugin.DeleteAsync(_key.Text) ? "Deleted" : "Could not delete")),
            },
        },
        new AdwPreferencesGroup("Result") { Rows = { new Watch(() => Demo.Fact("Last call", _status.Value)) } });

    private void Run(Func<Task<string>> call)
        => _ = call().ContinueWith(t => _status.Value = t.IsFaulted
            ? "Threw: " + t.Exception?.InnerException?.Message
            : t.Result);
}

// ── Share ────────────────────────────────────────────────────────────────────

/// <summary>Share: text and a file. On desktop the answer is Unavailable and the clipboard holds it.</summary>
internal sealed class SharePage : ComposedWidget
{
    private readonly Signal<string> _status = new("Nothing shared yet");
    private readonly AdwEntryRow _text = new("Text", "Zigote plugins: one API, five platforms");

    protected override Widget Build(BuildContext context) => Demo.Page(
        new AdwPreferencesGroup(
            "Share",
            "share_plus — a sheet on mobile; on desktop the payload goes to the clipboard and the answer is Unavailable")
        {
            Rows =
            {
                _text,
                Demo.Action("Share text", "ShareTextAsync(text, subject)", "Share", () => Run(
                    SharePlugin.ShareTextAsync(_text.Text, subject: "From the Zigote demo"))),
                Demo.Action("Share a file", "ShareFilesAsync([path], text)", "Share file", () =>
                {
                    string path = Path.Combine(PathProviderPlugin.Cache(), "zigote-demo.txt");
                    File.WriteAllText(path, _text.Text);
                    Run(SharePlugin.ShareFilesAsync([path], text: _text.Text));
                }),
            },
        },
        new AdwPreferencesGroup("Result") { Rows = { new Watch(() => Demo.Fact("Status", _status.Value)) } });

    private void Run(Task<ShareStatus> call)
        => _ = call.ContinueWith(t => _status.Value = t.IsFaulted
            ? "Threw: " + t.Exception?.InnerException?.Message
            : t.Result.ToString());
}

// ── Files ────────────────────────────────────────────────────────────────────

/// <summary>FilePicker: the native dialogs, with the picked path printed underneath.</summary>
internal sealed class FilesPage : ComposedWidget
{
    private readonly Signal<string> _picked = new("Nothing picked yet");

    protected override Widget Build(BuildContext context) => Demo.Page(
        new AdwPreferencesGroup("File picker", "image_picker + file_picker — portals on Linux, IFileDialog on Windows, NSOpenPanel on macOS")
        {
            Rows =
            {
                Demo.Action("Open a file", "OpenFileAsync(title, filters)", "Open", () => Run(
                    FilePickerPlugin.OpenFileAsync("Pick anything", [("Text", ["txt", "md"]), ("Images", ["png", "jpg"])]))),
                Demo.Action("Open several", "OpenFilesAsync()", "Open many", () => Run(
                    FilePickerPlugin.OpenFilesAsync("Pick a few").ContinueWith(t =>
                        t.Result.Length == 0 ? null : string.Join(", ", t.Result)))),
                Demo.Action("Pick a folder", "PickFolderAsync(title)", "Folder", () => Run(
                    FilePickerPlugin.PickFolderAsync("Pick a folder"))),
                Demo.Action("Save as", "SaveFileAsync(title, suggestedName)", "Save", () => Run(
                    FilePickerPlugin.SaveFileAsync("Save the demo", "zigote-demo.txt"))),
            },
        },
        new AdwPreferencesGroup("Result") { Rows = { new Watch(() => Demo.Fact("Picked", _picked.Value)) } });

    private void Run(Task<string?> call)
        => _ = call.ContinueWith(t => _picked.Value = t.IsFaulted
            ? "Threw: " + t.Exception?.InnerException?.Message
            : t.Result ?? "Cancelled");
}

// ── Links ────────────────────────────────────────────────────────────────────

/// <summary>
///     UrlLauncher and AppLinks. The interesting half is the second one: launch this app again
///     with a URL argument and the running copy receives it instead of a second window opening.
/// </summary>
internal sealed class LinksPage : ComposedWidget
{
    private readonly Signal<string> _incoming = new("No link received yet");
    private readonly Signal<string> _opened = new("Nothing opened yet");
    private readonly AdwEntryRow _url = new("URL", "https://example.org");
    private IDisposable? _subscription;

    protected override void OnMount()
        => _subscription = AppLinksPlugin.Listen(uri => _incoming.Value = $"{uri} at {DateTime.Now:HH:mm:ss}");

    public override void Detach()
    {
        _subscription?.Dispose();
        _subscription = null;
        base.Detach();
    }

    protected override Widget Build(BuildContext context) => Demo.Page(
        new AdwPreferencesGroup("Open a URL", "url_launcher — hands it to whatever the desktop registered")
        {
            Rows =
            {
                _url,
                Demo.Action("Open", "TryOpenAsync(url)", "Open", () =>
                    _ = UrlLauncherPlugin.TryOpenAsync(_url.Text).ContinueWith(t =>
                        _opened.Value = t.Result ? "Handed to the shell" : "No handler for that URL")),
                new Watch(() => Demo.Fact("Result", _opened.Value)),
            },
        },
        new AdwPreferencesGroup(
            "Incoming links",
            "app_links — run `pluginsdemo myapp://hello` in another terminal: this window receives it, no second window opens")
        {
            Rows =
            {
                Demo.Fact("Launched with", AppLinksPlugin.InitialLink?.ToString()),
                new Watch(() => Demo.Fact("Last received", _incoming.Value)),
            },
        });
}

// ── Notifications ────────────────────────────────────────────────────────────

/// <summary>Notifications: a real desktop notification through the freedesktop spec.</summary>
internal sealed class NotificationsPage : ComposedWidget
{
    private readonly NotificationClient _client = new("dev.zigote.PluginsDemo", "Zigote Plugins");
    private readonly Signal<string> _status = new("Not started");

    protected override void OnMount()
        => _ = _client.StartAsync().ContinueWith(t => _status.Value = t.IsFaulted
            ? "Could not start: " + t.Exception?.InnerException?.Message
            : $"Ready (actions {(_client.SupportsActions ? "supported" : "unsupported")})");

    public override void Detach()
    {
        _client.Dispose();
        base.Detach();
    }

    protected override Widget Build(BuildContext context) => Demo.Page(
        new AdwPreferencesGroup("Notifications", "flutter_local_notifications — one client, one slot per notification")
        {
            Rows =
            {
                new Watch(() => Demo.Fact("Transport", _status.Value)),
                Demo.Action("Show one", "client.Show(notification)", "Notify", () =>
                    _client.Show(new Notification("Zigote", "A notification from the plugin demo"))),
                Demo.Action("Close it", "client.Close()", "Close", () => _client.Close()),
            },
        });
}

// ── Location ─────────────────────────────────────────────────────────────────

/// <summary>Geolocation and Sensors — both unavailable on desktop, and both say so.</summary>
internal sealed class LocationPage : ComposedWidget
{
    private readonly Signal<string> _fix = new("No fix requested yet");
    private readonly Signal<string> _sample = new("Not listening");
    private IDisposable? _sensor;

    public override void Detach()
    {
        _sensor?.Dispose();
        _sensor = null;
        base.Detach();
    }

    protected override Widget Build(BuildContext context) => Demo.Page(
        new AdwPreferencesGroup(
            "Location",
            GeolocationPlugin.Available
                ? "geolocator — ask the Permissions plugin first"
                : "geolocator — no location service on this desktop, so every call answers null")
        {
            Rows =
            {
                Demo.Fact("Available", GeolocationPlugin.Available ? "Yes" : "No"),
                Demo.Action("Get a fix", "GetAsync(GeoAccuracy.Balanced)", "Locate", () =>
                    _ = GeolocationPlugin.GetAsync().ContinueWith(t => _fix.Value = t.Result is { } p
                        ? $"{p.Latitude:F5}, {p.Longitude:F5} ±{p.AccuracyMeters:F0} m"
                        : "null — unavailable or not permitted")),
                new Watch(() => Demo.Fact("Fix", _fix.Value)),
            },
        },
        new AdwPreferencesGroup("Sensors", "sensors_plus — m/s², rad/s and µT on every platform that has them")
        {
            Rows =
            {
                Demo.Fact("Accelerometer", SensorsPlugin.IsAvailable(SensorKind.Accelerometer) ? "Present" : "None"),
                Demo.Fact("Gyroscope", SensorsPlugin.IsAvailable(SensorKind.Gyroscope) ? "Present" : "None"),
                Demo.Action("Listen", "Listen(Accelerometer, …)", "Start", () =>
                {
                    _sensor?.Dispose();
                    _sensor = SensorsPlugin.Listen(SensorKind.Accelerometer, s =>
                        _sample.Value = $"x {s.X:F2}  y {s.Y:F2}  z {s.Z:F2}  |{s.Magnitude:F2}|");
                    if (!SensorsPlugin.IsAvailable(SensorKind.Accelerometer))
                        _sample.Value = "Subscribed — but this device has no accelerometer, so nothing will arrive";
                }),
                new Watch(() => Demo.Fact("Sample", _sample.Value)),
            },
        });
}

// ── Mobile ───────────────────────────────────────────────────────────────────

/// <summary>
///     The four that only mean something on a phone — Push, Biometrics, Haptics, AppSettings —
///     plus Permissions. On a desktop this page is the proof that the same code answers honestly
///     instead of throwing.
/// </summary>
internal sealed class MobilePage : ComposedWidget
{
    private readonly Signal<string> _auth = new("Not asked");
    private readonly Signal<string> _permission = new("Not asked");
    private readonly Signal<string> _token = new("Not registered");

    protected override Widget Build(BuildContext context) => Demo.Page(
        new AdwPreferencesGroup(
            "Push",
            "firebase_messaging, Firebase-free — the transport feeds the plugin over two channels")
        {
            Rows =
            {
                Demo.Fact("Available", PushPlugin.Available ? "Yes" : "No — desktop has no remote push"),
                Demo.Action("Register", "RegisterAsync(ct)", "Register", () =>
                    _ = PushPlugin.RegisterAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token)
                        .ContinueWith(t => _token.Value = t.Result ?? "null — unavailable or no transport")),
                new Watch(() => Demo.Fact("Token", _token.Value)),
            },
        },
        new AdwPreferencesGroup("Biometrics", "local_auth — the partner of SecureStorage")
        {
            Rows =
            {
                Demo.Fact("State", BiometricsPlugin.Check().ToString()),
                Demo.Action("Authenticate", "AuthenticateAsync(reason)", "Unlock", () =>
                    _ = BiometricsPlugin.AuthenticateAsync("Unlock the plugin demo")
                        .ContinueWith(t => _auth.Value = t.Result.ToString())),
                new Watch(() => Demo.Fact("Result", _auth.Value)),
            },
        },
        new AdwPreferencesGroup("Haptics", "vibration — a no-op where there is nothing to feel")
        {
            Rows =
            {
                Demo.Fact("Supported", HapticsPlugin.Supported ? "Yes" : "No"),
                Demo.Action("Selection tap", "Play(Haptic.Selection)", "Tap",
                    () => HapticsPlugin.Play(Haptic.Selection)),
                Demo.Action("Success pattern", "Play(Haptic.Success)", "Buzz",
                    () => HapticsPlugin.Play(Haptic.Success)),
            },
        },
        new AdwPreferencesGroup("System", "permission_handler + app_settings")
        {
            Rows =
            {
                Demo.Action("Ask for notifications", "RequestAsync(Notifications)", "Ask", () =>
                    _ = PermissionsPlugin.RequestAsync(ZigotePermission.Notifications)
                        .ContinueWith(t => _permission.Value = t.Result ? "Granted" : "Denied")),
                new Watch(() => Demo.Fact("Permission", _permission.Value)),
                Demo.Action("Open app settings", "OpenAsync(SettingsPage.App)", "Open",
                    () => _ = AppSettingsPlugin.OpenAsync()),
            },
        });
}
