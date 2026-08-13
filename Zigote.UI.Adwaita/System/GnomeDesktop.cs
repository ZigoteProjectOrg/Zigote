using System.Diagnostics;
using System.Globalization;

namespace Zigote.UI.Adwaita;

/// <summary>A window-frame button, in GNOME <c>button-layout</c> terms.</summary>
public enum AdwWindowButton
{
    Close,
    Minimize,
    Maximize,
}

/// <summary>
///     GNOME desktop settings the SDL layer does not surface: the accent color, the
///     <c>button-layout</c> (which window buttons exist and on which side of the titlebar), and
///     the color-scheme as a fallback for hosts where SDL reports Unknown. Read via
///     <c>gsettings</c> and kept fresh by <c>gsettings monitor</c> child processes — or, inside a
///     Flatpak/Snap sandbox, via the <c>org.freedesktop.portal.Settings</c> D-Bus interface, since
///     there the host's dconf is not reachable and <c>gsettings</c> answers with stock schema
///     defaults instead of the user's actual appearance. Consumers poll <see cref="ConsumeDirty" />
///     from the frame loop (the monitors write from background threads). Safe no-op on non-GNOME
///     systems — defaults mirror stock GNOME.
/// </summary>
public static class GnomeDesktop
{
    private static readonly object Gate = new();
    private static readonly List<Process> Monitors = [];
    private static bool _started;
    private static volatile bool _dirty;

    /// <summary>
    ///     Whether the app is sandboxed, and so has to ask the portal rather than read the host's
    ///     settings directly. Flatpak always writes <c>/.flatpak-info</c> into the sandbox; Snap
    ///     exports <c>SNAP</c>.
    /// </summary>
    private static readonly bool Sandboxed =
        OperatingSystem.IsLinux() &&
        (File.Exists("/.flatpak-info") ||
         Environment.GetEnvironmentVariable("SNAP") is { Length: > 0 });

    public static AdwAccent Accent { get; private set; } = AdwAccent.Blue;

    /// <summary>color-scheme prefers dark — fallback signal for when SDL reports Unknown.</summary>
    public static bool PrefersDark { get; private set; }

    /// <summary>Buttons on the left side of the titlebar, outermost first. Stock GNOME: none.</summary>
    public static IReadOnlyList<AdwWindowButton> LeftButtons { get; private set; } = [];

    /// <summary>Buttons on the right side of the titlebar. Stock GNOME: close only.</summary>
    public static IReadOnlyList<AdwWindowButton> RightButtons { get; private set; } =
        [AdwWindowButton.Close];

    public static bool IsGnome =>
        OperatingSystem.IsLinux() &&
        (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "")
        .Contains(value: "GNOME", comparisonType: StringComparison.OrdinalIgnoreCase);

    /// <summary>Read current values and start change monitoring. Idempotent; no-op off GNOME.</summary>
    public static void Start()
    {
        if (_started || !IsGnome) return;
        _started = true;
        Reread();
        if (Sandboxed)
        {
            // One monitor covers everything: the portal emits SettingChanged for every namespace.
            Monitor(
                exe: "gdbus",
                "monitor",
                "--session",
                "--dest",
                "org.freedesktop.portal.Desktop"
            );
        }
        else
        {
            Monitor(exe: "gsettings", "monitor", "org.gnome.desktop.interface");
            Monitor(exe: "gsettings", "monitor", "org.gnome.desktop.wm.preferences");
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            lock (Gate)
            {
                foreach (var p in Monitors)
                {
                    try
                    {
                        p.Kill();
                    }
                    catch
                    {
                        /* already gone */
                    }
                }
            }
        };
    }

    /// <summary>
    ///     True once after any monitored setting changed; the caller then re-reads the
    ///     properties (already refreshed) and reapplies.
    /// </summary>
    public static bool ConsumeDirty()
    {
        if (!_dirty) return false;
        _dirty = false;
        return true;
    }

    private static void Reread()
    {
        if (Sandboxed)
        {
            RereadPortal();
            return;
        }

        string? accent = Gsettings(schema: "org.gnome.desktop.interface", key: "accent-color");
        if (accent is not null) Accent = ParseAccent(accent);

        string? scheme = Gsettings(schema: "org.gnome.desktop.interface", key: "color-scheme");
        if (scheme is not null) PrefersDark = scheme.Contains("prefer-dark");

        string? layout = Gsettings(
            schema: "org.gnome.desktop.wm.preferences",
            key: "button-layout"
        );
        if (layout is not null) (LeftButtons, RightButtons) = ParseButtonLayout(layout);
    }

    /// <summary>
    ///     The sandboxed read. Appearance comes from the cross-desktop
    ///     <c>org.freedesktop.appearance</c> namespace every portal backend implements: color-scheme
    ///     as an enum and the accent as an sRGB triple, which is matched back to the nearest named
    ///     libadwaita hue. <c>button-layout</c> has no standard portal key, so it is asked for by its
    ///     GNOME schema name and simply left at the stock default when the backend does not answer.
    /// </summary>
    private static void RereadPortal()
    {
        string? scheme = Portal(ns: "org.freedesktop.appearance", key: "color-scheme");
        // 0 = no preference, 1 = prefer dark, 2 = prefer light.
        if (scheme is not null && int.TryParse(s: LastToken(scheme), result: out int pref))
            PrefersDark = pref == 1;

        string? accent = Portal(ns: "org.freedesktop.appearance", key: "accent-color");
        if (accent is not null && ParseAccentRgb(accent) is { } hue) Accent = hue;

        string? layout = Portal(ns: "org.gnome.desktop.wm.preferences", key: "button-layout");
        if (layout is not null) (LeftButtons, RightButtons) = ParseButtonLayout(layout);
    }

    /// <summary>Nearest named accent to a portal <c>(ddd)</c> sRGB triple, e.g. <c>(0.2, 0.5, 0.9)</c>.</summary>
    private static AdwAccent? ParseAccentRgb(string value)
    {
        string[] parts = value.Trim().Trim('(', ')').Split(',');
        if (parts.Length != 3) return null;

        Span<float> rgb = stackalloc float[3];
        for (int i = 0; i < 3; i++)
        {
            if (!float.TryParse(
                    s: parts[i].Trim(),
                    style: NumberStyles.Float,
                    provider: CultureInfo.InvariantCulture,
                    result: out rgb[i]
                ))
                return null;
        }

        return AdwAccentColors.Nearest(r: rgb[0], g: rgb[1], b: rgb[2]);
    }

    /// <summary>The last whitespace-separated token — strips gdbus's type prefix (<c>uint32 1</c>).</summary>
    private static string LastToken(string value)
    {
        string[] parts = value.Split(
            separator: ' ',
            options: StringSplitOptions.RemoveEmptyEntries
        );
        return parts.Length == 0 ? value : parts[^1];
    }

    private static AdwAccent ParseAccent(string value)
    {
        return value.Trim('\'', ' ') switch {
            "teal" => AdwAccent.Teal,
            "green" => AdwAccent.Green,
            "yellow" => AdwAccent.Yellow,
            "orange" => AdwAccent.Orange,
            "red" => AdwAccent.Red,
            "pink" => AdwAccent.Pink,
            "purple" => AdwAccent.Purple,
            "slate" => AdwAccent.Slate,
            _ => AdwAccent.Blue,
        };
    }

    /// <summary>
    ///     Parse metacity <c>button-layout</c>: colon splits left from right, commas separate
    ///     buttons, non-button tokens (appmenu, spacer, icon) are ignored. No colon → all right.
    /// </summary>
    internal static (AdwWindowButton[] Left, AdwWindowButton[] Right) ParseButtonLayout(
        string value)
    {
        string raw = value.Trim('\'', ' ');
        int colon = raw.IndexOf(':');
        string left = colon < 0 ? "" : raw[..colon];
        string right = colon < 0 ? raw : raw[(colon + 1)..];
        return (Parse(left), Parse(right));

        static AdwWindowButton[] Parse(string side)
        {
            var list = new List<AdwWindowButton>(3);
            foreach (string token in side.Split(
                         separator: ',',
                         options: StringSplitOptions.RemoveEmptyEntries
                     ))
            {
                switch (token.Trim())
                {
                    case "close": list.Add(AdwWindowButton.Close); break;
                    case "minimize": list.Add(AdwWindowButton.Minimize); break;
                    case "maximize": list.Add(AdwWindowButton.Maximize); break;
                }
            }

            return list.ToArray();
        }
    }

    private static string? Gsettings(string schema, string key)
    {
        return Run(
            exe: "gsettings",
            "get",
            schema,
            key
        );
    }

    /// <summary>
    ///     Read one setting through <c>org.freedesktop.portal.Settings</c>, unwrapped from gdbus's
    ///     variant syntax. <c>ReadOne</c> is the version-2 method; the fallback to the deprecated
    ///     <c>Read</c> (whose result is wrapped one level deeper) keeps older portal backends working.
    ///     Null means the backend does not know the key — the caller keeps its current value.
    /// </summary>
    private static string? Portal(string ns, string key)
    {
        string? raw = PortalCall(method: "ReadOne", ns: ns, key: key) ??
                      PortalCall(method: "Read", ns: ns, key: key);
        // `(<uint32 1>,)` and the v1 `(<<uint32 1>>,)` both unwrap to the value itself.
        return raw?.Trim().Trim('(', ')', ',').Trim().Trim('<', '>').Trim();
    }

    private static string? PortalCall(string method, string ns, string key)
    {
        return Run(
            exe: "gdbus",
            "call",
            "--session",
            "--dest",
            "org.freedesktop.portal.Desktop",
            "--object-path",
            "/org/freedesktop/portal/desktop",
            "--method",
            $"org.freedesktop.portal.Settings.{method}",
            ns,
            key
        );
    }

    /// <summary>Run a helper and return its trimmed stdout, or null if it is missing or fails.</summary>
    private static string? Run(string exe, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(exe) {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string arg in args) info.ArgumentList.Add(arg);
            using var p = Process.Start(info);
            if (p is null) return null;
            string output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            return p.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch
        {
            return null; // helper not on PATH — stock defaults stand.
        }
    }

    private static void Monitor(string exe, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(exe) {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (string arg in args) info.ArgumentList.Add(arg);
            var p = new Process {
                StartInfo = info,
                EnableRaisingEvents = true,
            };
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                // gdbus monitor reports every signal from the portal; only settings matter here.
                if (exe == "gdbus" && !e.Data.Contains("SettingChanged")) return;
                Reread();
                _dirty = true;
            };
            if (!p.Start()) return;
            p.BeginOutputReadLine();
            lock (Gate) Monitors.Add(p);
        }
        catch
        {
            // No monitoring — values stay as read at startup.
        }
    }
}
