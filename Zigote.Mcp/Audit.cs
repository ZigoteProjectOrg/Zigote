using System.Text;
using System.Text.Json.Nodes;

namespace Zigote.Mcp;

/// <summary>
///     <c>audit</c>: the accessibility and polish lint a reviewer runs by eye — text contrast,
///     controls too small to hit or with nothing for a screen reader to say, text cut off — answered
///     from the semantics tree, the frame's text runs and one capture, so the model gets a checklist
///     instead of guessing from a screenshot.
/// </summary>
public static partial class Tools
{
    private const int MaxFindings = 25;

    private static readonly HashSet<string> InteractiveRoles =
        ["Button", "Link", "Checkbox", "RadioButton", "Switch", "Slider", "TextField", "Tab", "MenuItem"];

    private static JsonObject AuditUi(JsonObject args)
    {
        int port = Port(args);
        double minTarget = Num(args: args, name: "min_target", fallback: 24);
        // First: capturing waits for animations to settle, so the tree and runs read after it
        // describe the same frame as the pixels.
        (byte[] bmp, double density, _) = Capture(port: port, scale: 1);
        (int pw, int ph, int[] pixels) = Png.Pixels(bmp);
        var tree = Tree(port: port, command: "semantics");
        double w = tree is JsonObject root ? N(root, "w") : 0, h = tree is JsonObject r ? N(r, "h") : 0;

        // ── controls: a name to announce, a size to hit ──
        var unlabelled = new List<string>();
        var small = new List<string>();
        var disabled = new List<JsonObject>();
        foreach (var node in Nodes(tree))
        {
            if (Offscreen(node: node, w: w, h: h)) continue;
            if (Has(node, "flags", "Disabled"))
            {
                disabled.Add(node);
                continue;
            }

            string role = S(node, "role") ?? "";
            if (!InteractiveRoles.Contains(role) && !Has(node, "actions", "Tap")) continue;
            // A button's name is often its child's text, which is how a reader reaches it too.
            if (!Nodes(node).Any(n => S(n, "label") is { Length: > 0 } || S(n, "value") is { Length: > 0 }))
                unlabelled.Add($"{role} {Dim(node)} @{F(Center(node).X)},{F(Center(node).Y)}");
            if (N(node, "w") < minTarget || N(node, "h") < minTarget)
                small.Add($"{Describe(node)} {Dim(node)}");
        }

        // ── text: contrast against what is actually behind it, and runs cut short ──
        var runs = Runs(port: port, region: "");
        var contrast = new Dictionary<string, (double Ratio, int Count, string First)>();
        var cut = new List<string>();
        foreach (var run in runs)
        {
            string name = run.Icon ? "icon" : $"\"{Short(run.Text)}\"";
            string at = $"@{F(run.X)},{F(run.Baseline)}";
            if (run.Clip is [var cx, _, var cw, _] && (run.X < cx - 0.5 || run.X + run.W > cx + cw + 0.5))
                cut.Add($"{name} clipped to x {F(cx)}–{F(cx + cw)} {at}");
            else if (w > 0 && run.X + run.W > w + 0.5) cut.Add($"{name} runs past the window edge {at}");
            else if (run.Text.EndsWith('…')) cut.Add($"{name} ellipsized {at}");

            if (disabled.Any(d => Inside(d, run.X, run.Baseline))) continue; // WCAG exempts inactive controls
            if (Hex(run.Color) is not { } fg) continue;
            if (Background(run, fg.Rgb) is not { } bg) continue;
            int text = fg.Alpha >= 1 ? fg.Rgb : Blend(fg.Rgb, bg, fg.Alpha);
            double ratio = Ratio(text, bg);
            bool large = run.Icon || run.Size >= 24 || (run.Size >= 18.66 && run.Weight >= 700);
            if (ratio >= (large ? 3 : 4.5)) continue;

            string key = $"{F(ratio)}:1 {(run.Icon ? "icon" : $"{F(run.Size)}/{run.Weight}")} #{text:x6} on #{bg:x6}";
            contrast[key] = contrast.TryGetValue(key, out var seen)
                ? seen with { Count = seen.Count + 1 }
                : (ratio, 1, $"{name} {at}");
        }

        int total = contrast.Count + small.Count + unlabelled.Count + cut.Count;
        var s = new StringBuilder(
            $"{F(w)}x{F(h)}: {contrast.Count} contrast · {small.Count} small target · " +
            $"{unlabelled.Count} unlabelled · {cut.Count} cut-off text");
        if (total == 0)
            return Text($"{s} — clean ({runs.Count} text runs checked)");

        if (contrast.Count > 0)
        {
            s.Append("\ncontrast (WCAG AA: 4.5, large text and icons 3) — ratio size/weight text on bg, first seen:");
            foreach ((string key, var v) in contrast.OrderBy(kv => kv.Value.Ratio).Take(MaxFindings))
                s.Append($"\n  {key} {v.First}{(v.Count > 1 ? $" (+{v.Count - 1} more)" : "")}");
        }

        Section($"targets under {F(minTarget)}pt", small);
        Section("interactive with no label for a screen reader", unlabelled);
        Section("cut-off text", cut);
        return Text(s.ToString());

        void Section(string title, List<string> items)
        {
            if (items.Count == 0) return;
            s.Append($"\n{title}:");
            foreach (string item in items.Take(MaxFindings)) s.Append("\n  ").Append(item);
            if (items.Count > MaxFindings) s.Append($"\n  … {items.Count - MaxFindings} more");
        }

        // The run's background is the commonest colour in its em box that is clearly not the ink:
        // glyphs cover a minority of the box, and anti-aliased edges spread over many shades.
        int? Background(Run run, int ink)
        {
            int x0 = (int)((run.X - 2) * density), x1 = (int)Math.Ceiling((run.X + run.W + 2) * density);
            int y0 = (int)((run.Baseline - run.Size) * density), y1 = (int)Math.Ceiling((run.Baseline + (run.Size / 4)) * density);
            var counts = new Dictionary<int, int>();
            for (int y = Math.Max(0, y0); y < Math.Min(ph, y1); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(pw, x1); x++)
            {
                int c = pixels[(y * pw) + x];
                if (Ratio(c, ink) >= 1.1) counts[c] = counts.GetValueOrDefault(c) + 1;
            }

            return counts.Count == 0 ? null : counts.MaxBy(kv => kv.Value).Key;
        }

        static bool Inside(JsonObject n, double x, double y) =>
            x >= N(n, "x") && y >= N(n, "y") && x <= N(n, "x") + N(n, "w") && y <= N(n, "y") + N(n, "h");
    }

    private static (int Rgb, double Alpha)? Hex(string color) =>
        color.Length is 7 or 9 && color[0] == '#' && int.TryParse(color.AsSpan(1, 6), System.Globalization.NumberStyles.HexNumber, null, out int rgb)
            ? (rgb, color.Length == 9 ? Convert.ToInt32(color[7..], 16) / 255.0 : 1)
            : null;

    private static int Blend(int fg, int bg, double a)
    {
        int Ch(int shift) => (int)Math.Round((((fg >> shift) & 255) * a) + (((bg >> shift) & 255) * (1 - a)));
        return (Ch(16) << 16) | (Ch(8) << 8) | Ch(0);
    }

    /// <summary>WCAG 2 contrast ratio of two sRGB colours, 1–21.</summary>
    private static double Ratio(int a, int b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);

        static double Luminance(int c)
        {
            static double Lin(int v)
            {
                double s = v / 255.0;
                return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * Lin((c >> 16) & 255)) + (0.7152 * Lin((c >> 8) & 255)) + (0.0722 * Lin(c & 255));
        }
    }
}
