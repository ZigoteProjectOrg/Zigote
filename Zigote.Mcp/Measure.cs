using System.Text;
using System.Text.Json.Nodes;

namespace Zigote.Mcp;

/// <summary>
///     <c>measure</c>: what a designer checks with a ruler — baselines, insets, gaps, centering —
///     answered from the app's own text runs (the inspect <c>text</c> command) and widget bounds,
///     so the model never has to eyeball pixels in a screenshot.
/// </summary>
public static partial class Tools
{
    // Inter's cap height (Iosevka is within 0.01 em). Text is optically centred on its caps, which
    // is also what TextBaseline.Centered aims for; icons are centred on their em box.
    private const double CapHeight = 0.727;

    // Under this a difference is rounding, not a design decision; over the near-miss ceiling it is
    // presumably intended (a deliberate indent, a different row).
    private const double Noise = 0.25;
    private const double NearMiss = 3;

    private const int MaxRuns = 150;

    private sealed record Box(string Name, double X, double Y, double W, double H, bool Optical)
    {
        public double Right => X + W;
        public double Bottom => Y + H;
        public double Cx => X + (W / 2);
        public double Cy => Y + (H / 2);

        public bool Contains(Box o) =>
            o.X >= X - 0.5 && o.Y >= Y - 0.5 && o.Right <= Right + 0.5 && o.Bottom <= Bottom + 0.5;

        public override string ToString() => $"{Name} [{F(X)},{F(Y)} {F(W)}x{F(H)}]";
    }

    private sealed record Run(string Text, double X, double Baseline, double Size, double W, int Weight, string Color, double[]? Clip = null)
    {
        public bool Icon => Text == "icon";

        // The box the eye centres: caps for text, the em square for an icon glyph.
        public Box Optical => Icon
            ? new Box("icon", X, Baseline - Size, W, Size, true)
            : new Box($"\"{Short(Text)}\"", X, Baseline - (Size * CapHeight), W, Size * CapHeight, true);
    }

    private static JsonObject Measure(JsonObject args)
    {
        int port = Port(args);
        string? a = StrOpt(args: args, name: "a"), b = StrOpt(args: args, name: "b");
        if ((a is null) != (b is null)) throw new ToolError("measure wants both a and b, or neither (audit)");

        var runs = Runs(port: port, region: args["region"] is JsonArray { Count: 4 } r
            ? $" {F(r[0]!.GetValue<double>())} {F(r[1]!.GetValue<double>())} {F(r[2]!.GetValue<double>())} {F(r[3]!.GetValue<double>())}"
            : "");
        return Text(a is null ? Audit(port, runs) : Pair(Resolve(port, a, runs), Resolve(port, b!, runs)));
    }

    private static List<Run> Runs(int port, string region)
    {
        var reply = Parse(Send(port: port, command: "text" + region));
        if (reply?["error"] is { } error)
            throw new ToolError($"{error} — an app built before the `text` command needs a rebuild");
        return [.. (reply?["runs"] as JsonArray ?? []).OfType<JsonObject>().Select(n => new Run(
            S(n, "t") ?? "", N(n, "x"), N(n, "baseline"), N(n, "size"), N(n, "w"), (int)N(n, "weight"), S(n, "color") ?? "",
            n["clip"] is JsonArray { Count: 4 } c ? [.. c.Select(v => v!.GetValue<double>())] : null))];
    }

    private static Box Resolve(int port, string spec, List<Run> runs)
    {
        if (spec.StartsWith('#') && int.TryParse(spec[1..], out int id))
        {
            var props = Parse(Send(port: port, command: $"props {id}")) as JsonObject;
            if (props?["error"] is { } e) throw new ToolError(e.ToString());
            return new Box($"{S(props!, "type")}#{id}", N(props!, "x"), N(props!, "y"), N(props!, "w"), N(props!, "h"), false);
        }

        double[] xywh = [.. spec.Split(',').Select(s => double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : double.NaN)];
        if (xywh.Length == 4 && !xywh.Any(double.IsNaN))
            return new Box("rect", xywh[0], xywh[1], xywh[2], xywh[3], false);

        bool textOnly = spec.StartsWith("text:");
        string needle = textOnly ? spec[5..] : spec;
        if (!textOnly)
        {
            (var hits, double w, double h) = SemanticsMatches(port: port, label: needle, role: null);
            if (hits.FirstOrDefault(n => !Offscreen(node: n, w: w, h: h)) is { } node)
            {
                return new Box($"{S(node, "role")} '{S(node, "label") ?? S(node, "value")}'",
                    N(node, "x"), N(node, "y"), N(node, "w"), N(node, "h"), false);
            }
        }

        var run = runs.FirstOrDefault(r => r.Text == needle) ??
                  runs.FirstOrDefault(r => r.Text.Contains(needle, StringComparison.OrdinalIgnoreCase)) ??
                  throw new ToolError($"nothing visible matches '{needle}' (semantic label or drawn text)");
        return run.Optical with { Name = $"text {run.Optical.Name} base {F(run.Baseline)} {F(run.Size)}px" };
    }

    private static string Pair(Box a, Box b)
    {
        var s = new StringBuilder();
        s.Append($"a {a}\nb {b}\n");
        s.Append($"gap  x {F(Gap(a.X, a.Right, b.X, b.Right))}  y {F(Gap(a.Y, a.Bottom, b.Y, b.Bottom))}  (negative = overlap)\n");
        s.Append($"Δ b−a  left {F(b.X - a.X)}  right {F(b.Right - a.Right)}  top {F(b.Y - a.Y)}  bottom {F(b.Bottom - a.Bottom)}  cx {F(b.Cx - a.Cx)}  cy {F(b.Cy - a.Cy)}");
        if (a.Contains(b)) s.Append('\n').Append(Inset(outer: a, inner: b, "b in a"));
        else if (b.Contains(a)) s.Append('\n').Append(Inset(outer: b, inner: a, "a in b"));
        return s.ToString();

        // Distance between two spans along one axis; negative when they overlap.
        static double Gap(double a0, double a1, double b0, double b1) => Math.Max(b0 - a1, a0 - b1);
    }

    private static string Inset(Box outer, Box inner, string what) =>
        $"{what}: insets l {F(inner.X - outer.X)} r {F(outer.Right - inner.Right)} t {F(inner.Y - outer.Y)} b {F(outer.Bottom - inner.Bottom)}" +
        $" · off-center x {F(inner.Cx - outer.Cx)} y {F(inner.Cy - outer.Cy)}" +
        (inner.Optical ? " (by cap height)" : "");

    private static string Audit(int port, List<Run> runs)
    {
        if (runs.Count == 0) return "no visible text runs";

        // Candidate boxes: every widget that is not itself text. A run's box is the smallest one
        // that contains it and is clearly taller than a line — a button, a row, a header bar.
        var widgets = Nodes(Tree(port: port, command: "widgets"))
                        // Padding is skipped: its asymmetric insets are the point, and the box it pads is the real one.
            .Where(n => S(n, "type") is not ("Label" or "RichText" or "IconGlyph" or "Text" or "Padding") && N(n, "w") > 0)
            .Select(n => new Box($"{S(n, "type")}#{n["id"]}", N(n, "x"), N(n, "y"), N(n, "w"), N(n, "h"), false))
            .ToList();

        var s = new StringBuilder();
        var sorted = runs.OrderBy(r => Math.Round(r.Baseline)).ThenBy(r => r.X).ToList();
        s.Append($"{runs.Count} text run(s) — x, baseline, size/weight, color; box = smallest non-text widget around it,\n");
        s.Append("centering error = optical center (caps / icon em box) minus box center; x only when it looks centered\n");
        foreach (var run in sorted.Take(MaxRuns))
        {
            s.Append($"\n{(run.Icon ? "icon" : $"\"{Short(run.Text)}\""),-28} x {F(run.X),-7} base {F(run.Baseline),-7} {F(run.Size)}/{run.Weight} w {F(run.W)} {run.Color}");
            var o = run.Optical;
            if (widgets.Where(w => w.Contains(o) && w.H >= run.Size * 1.6).MinBy(w => w.W * w.H) is not { } box) continue;
            double dy = o.Cy - box.Cy;
            double insetL = o.X - box.X, insetR = box.Right - o.Right;
            s.Append($"  in {box.Name} {F(box.W)}x{F(box.H)} l {F(insetL)}");
            // Centering is only meaningful for a box that holds this one line (a title over a
            // subtitle is not centred on either), and horizontally only when it looks intended.
            if (runs.Any(r => r != run && Math.Abs(r.Baseline - run.Baseline) > Noise && box.Contains(r.Optical))) continue;
            if (box.H < run.Size * 3) s.Append($" dy {Flag(dy)}");
            if (Math.Abs(insetL - insetR) <= 8) s.Append($" dx {Flag(o.Cx - box.Cx)}");
        }

        if (runs.Count > MaxRuns) s.Append($"\n… {runs.Count - MaxRuns} more — pass region to narrow");

        // Near-misses: what reads as "almost aligned" — the bugs a ruler finds and an eye half-sees.
        var issues = new List<string>();
        for (int i = 0; i < sorted.Count; i++)
        {
            for (int j = i + 1; j < sorted.Count; j++)
            {
                Run p = sorted[i], q = sorted[j];
                var (po, qo) = (p.Optical, q.Optical);
                bool sameRow = po.Y < qo.Bottom && qo.Y < po.Bottom &&
                               Math.Max(qo.X - po.Right, po.X - qo.Right) < 160;
                double db = Math.Abs(p.Baseline - q.Baseline);
                if (sameRow && !p.Icon && !q.Icon && db > Noise && db <= NearMiss * 2)
                    issues.Add($"baseline Δ{F(db)}: {Label(p)} {F(p.Baseline)} vs {Label(q)} {F(q.Baseline)}");
                double dx = Math.Abs(p.X - q.X);
                if (!sameRow && LineStart(p) && LineStart(q) && dx > Noise && dx <= NearMiss && Math.Abs(p.Baseline - q.Baseline) < 240)
                    issues.Add($"left edge Δ{F(dx)}: {Label(p)} x {F(p.X)} vs {Label(q)} x {F(q.X)}");
            }
        }

        s.Append(issues.Count == 0 ? "\n\nno near-miss alignments" : $"\n\nnear-misses ({issues.Count}):");
        foreach (string issue in issues.Distinct().Take(40)) s.Append("\n  ").Append(issue);
        return s.ToString();

        // A run that begins a line, not a styled span in the middle of one (" → ", "`code`").
        bool LineStart(Run r) => !runs.Any(o => o != r && Math.Abs(o.Baseline - r.Baseline) < 1 && o.X < r.X && o.X + o.W >= r.X - 4);

        static string Label(Run r) => r.Icon ? "icon" : $"\"{Short(r.Text)}\"";
        // A centering error worth a look is marked; sub-noise ones print as 0.
        static string Flag(double d) => Math.Abs(d) < Noise ? "0" : Math.Abs(d) >= 0.75 ? $"{F(d)}!" : F(d);
    }

    private static string Short(string text) => text.Length > 24 ? text[..23] + "…" : text.ReplaceLineEndings(" ");
}
