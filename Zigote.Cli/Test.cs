namespace Zigote.Cli;

/// <summary>
///     <c>zigote test</c> — the app's own tests, and the golden images they compare against.
///     <para>
///         The runner half is a wrapper over <c>dotnet test</c>, and deliberately so. The half that
///         is not a wrapper is the golden convention, which exists because "the pixels changed"
///         is the least readable failure in a UI framework: a test that renders a widget writes
///         <c>&lt;name&gt;.actual.png</c> beside the expected <c>&lt;name&gt;.png</c> when they
///         differ, and this command turns each of those pairs into one three-panel image —
///         expected, actual, and the difference — which is a thing a person can look at.
///     </para>
///     <para>
///         <c>--update-goldens</c> is the other half of the same convention: it sets
///         <c>ZIGOTE_UPDATE_GOLDENS=1</c>, and a harness that sees it overwrites the expected image
///         instead of failing. Accepting a rendering change should be one flag, not a manual copy
///         of files whose names you had to look up.
///     </para>
/// </summary>
public static class Test
{
    public static int Run(TestVerb options)
    {
        var projects = TestProjects(options.Directory);
        if (projects.Count == 0)
        {
            throw new CliError(
                $"no test project under '{options.Directory}'. A file named *Tests.csproj or " +
                "*.Tests.csproj is what this looks for."
            );
        }

        int exit = 0;
        foreach (string project in projects)
        {
            Console.WriteLine($"  {Path.GetFileNameWithoutExtension(project)}");
            var start = Shell.Command(
                file: "dotnet",
                arguments: options.Filter is { } filter
                    ? ["test", project, "--nologo", "--filter", filter]
                    : ["test", project, "--nologo"]
            );
            if (options.UpdateGoldens) start.Environment["ZIGOTE_UPDATE_GOLDENS"] = "1";

            int result = Shell.Wait(start);
            if (result != 0) exit = result;
        }

        int diffs = WriteGoldenDiffs(options.Directory);
        if (diffs > 0)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"  {diffs} golden{(diffs == 1 ? "" : "s")} differ — see the *.diff.png beside each one"
            );
            Console.WriteLine(
                "  (expected | actual | difference). Accept them with --update-goldens."
            );
        }

        return exit;
    }

    /// <summary>
    ///     Turn every <c>x.actual.png</c> next to an <c>x.png</c> into <c>x.diff.png</c>: the two
    ///     images side by side, and a third panel where every pixel that moved is drawn in magenta
    ///     over a dimmed copy. Magenta because it appears in almost no real UI, so a changed pixel
    ///     cannot be mistaken for content.
    /// </summary>
    private static int WriteGoldenDiffs(string root)
    {
        int count = 0;
        foreach (string actualPath in Directory.EnumerateFiles(
                     path: root,
                     searchPattern: "*.actual.png",
                     searchOption: SearchOption.AllDirectories
                 ))
        {
            string expectedPath = actualPath.Replace(
                oldValue: ".actual.png",
                newValue: ".png",
                comparisonType: StringComparison.Ordinal
            );
            if (!File.Exists(expectedPath)) continue;

            var expected = Png.Load(expectedPath);
            var actual = Png.Load(actualPath);
            Compose(expected: expected, actual: actual).Save(
                actualPath.Replace(
                    oldValue: ".actual.png",
                    newValue: ".diff.png",
                    comparisonType: StringComparison.Ordinal
                )
            );
            count++;
        }

        return count;
    }

    /// <summary>The three panels, on one canvas sized to the larger of the two images.</summary>
    internal static Png Compose(Png expected, Png actual)
    {
        int width = Math.Max(val1: expected.Width, val2: actual.Width);
        int height = Math.Max(val1: expected.Height, val2: actual.Height);
        byte[] canvas = new byte[width * 3 * height * 4];

        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            (byte er, byte eg, byte eb, byte ea) = Sample(image: expected, x: x, y: y);
            (byte ar, byte ag, byte ab, byte aa) = Sample(image: actual, x: x, y: y);
            Put(
                canvas: canvas,
                stride: width * 3,
                x: x,
                y: y,
                r: er,
                g: eg,
                b: eb,
                a: ea
            );
            Put(
                canvas: canvas,
                stride: width * 3,
                x: width + x,
                y: y,
                r: ar,
                g: ag,
                b: ab,
                a: aa
            );

            bool same = er == ar && eg == ag && eb == ab && ea == aa;
            Put(
                canvas: canvas,
                stride: width * 3,
                x: (width * 2) + x,
                y: y,
                r: same ? (byte)(er / 3) : (byte)255,
                g: same ? (byte)(eg / 3) : (byte)0,
                b: same ? (byte)(eb / 3) : (byte)255,
                a: 255
            );
        }

        return Png.FromPixels(width: width * 3, height: height, rgba: canvas);
    }

    /// <summary>Transparent outside the image, so two differently-sized renders still line up.</summary>
    private static (byte R, byte G, byte B, byte A) Sample(Png image, int x, int y)
    {
        if (x >= image.Width || y >= image.Height) return (0, 0, 0, 0);
        int at = ((y * image.Width) + x) * 4;
        return (image.Pixels[at], image.Pixels[at + 1], image.Pixels[at + 2], image.Pixels[at + 3]);
    }

    private static void Put(byte[] canvas, int stride, int x, int y, byte r, byte g, byte b, byte a)
    {
        int at = ((y * stride) + x) * 4;
        canvas[at] = r;
        canvas[at + 1] = g;
        canvas[at + 2] = b;
        canvas[at + 3] = a;
    }

    private static List<string> TestProjects(string root) =>
        Directory
            .EnumerateFiles(
                path: root,
                searchPattern: "*.csproj",
                searchOption: SearchOption.AllDirectories
            )
            .Where(p =>
                !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
            )
            .Where(p =>
                !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
            )
            .Where(p => Path.GetFileNameWithoutExtension(p)
                .EndsWith(value: "Tests", comparisonType: StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
}
