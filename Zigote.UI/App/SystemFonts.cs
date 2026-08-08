using Zigote.Core.Engine;

namespace Zigote.UI.Host;

/// <summary>
///     Finds the platform's own faces for the scripts the bundled UI font does not cover, and
///     registers them as fallbacks.
///     <para>
///         Inter draws Latin, Greek and Cyrillic. Everything else — Japanese, Korean, Chinese,
///         Arabic, Hebrew, Thai, Devanagari — is somebody else's font. Bundling coverage for all of
///         them would add well over a hundred megabytes, and the result would still look foreign on
///         each platform, because every OS has a house face its users already read everything else
///         in. So the faces are borrowed from the system.
///     </para>
///     <para>
///         Each entry lists candidates in preference order, and the first file that exists wins.
///         The preference is for the face that sits closest to Inter: a humanist/neo-grotesque sans
///         at a similar weight and x-height, and the one the platform itself uses for UI, so mixed
///         text reads as one typeface rather than a ransom note.
///     </para>
/// </summary>
public static class SystemFonts
{
    /// <summary>
    ///     Candidate files per platform, in the order they should be tried.
    ///     <para>
    ///         Ordering matters twice over: it is the order a missing glyph is searched in, so the
    ///         broadest, best-matching face should come first, and CJK faces must precede any
    ///         pan-Unicode face — Noto Sans CJK carries Latin too, and letting it answer first
    ///         would quietly replace Inter for ordinary text.
    ///     </para>
    /// </summary>
    private static IEnumerable<string> Candidates()
    {
        if (OperatingSystem.IsMacOS())
            return [
                // Apple's own UI faces. Hiragino Sans is what macOS sets Japanese in; PingFang is
                // the Chinese counterpart and Apple SD Gothic Neo the Korean one. All three are
                // grotesques drawn to sit beside San Francisco, so they sit beside Inter too.
                "/System/Library/Fonts/ヒラギノ角ゴシック W3.ttc",
                "/System/Library/Fonts/Hiragino Sans GB.ttc",
                "/System/Library/Fonts/PingFang.ttc",
                "/System/Library/Fonts/AppleSDGothicNeo.ttc",
                "/System/Library/Fonts/Supplemental/Arial Unicode.ttf",
                "/Library/Fonts/Arial Unicode.ttf",
            ];

        if (OperatingSystem.IsWindows())
        {
            var fonts = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Fonts"
            );
            return new[] {
                // The UI variants, not the text ones: they are the faces Windows itself sets menus
                // and labels in, and they are tuned for screen sizes rather than print.
                "YuGothM.ttc", // Yu Gothic Medium — Japanese
                "YuGothR.ttc",
                "msyh.ttc", // Microsoft YaHei — Simplified Chinese
                "msjh.ttc", // Microsoft JhengHei — Traditional Chinese
                "malgun.ttf", // Malgun Gothic — Korean
                "segoeui.ttf", // Segoe UI — Arabic, Hebrew, Thai and the rest
            }.Select(name => Path.Combine(fonts, name));
        }

        // Linux and the BSDs have no fixed layout — the same Noto CJK ships as
        // noto-cjk/NotoSansCJK-Regular.ttc on one distribution and
        // google-noto-sans-cjk-vf-fonts/NotoSansCJK-VF.ttc on another — so ask fontconfig, which is
        // the component whose whole job this is and which every desktop already has.
        var matched = FontconfigMatches().ToList();

        // If fontconfig is missing or answered nothing, try the paths that are common enough to be
        // worth guessing at.
        return matched.Count > 0
            ? matched
            : [
                "/usr/share/fonts/noto-cjk/NotoSansCJK-Regular.ttc",
                "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
                "/usr/share/fonts/truetype/noto/NotoSansCJK-Regular.ttc",
                // Source Han Sans is the same design under its Adobe name.
                "/usr/share/fonts/adobe-source-han-sans/SourceHanSans-Regular.otf",
                // Last resorts that at least cover Arabic, Hebrew, Thai and Devanagari.
                "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
                "/usr/share/fonts/dejavu-sans-fonts/DejaVuSans.ttf",
            ];
    }

    /// <summary>
    ///     The scripts worth asking fontconfig about, in fallback priority order. CJK first, both
    ///     because it is the most common gap and because its faces carry the widest coverage.
    /// </summary>
    private static readonly string[] Languages =
        ["ja", "ko", "zh-cn", "zh-tw", "ar", "he", "th", "hi"];

    /// <summary>
    ///     Ask <c>fc-match</c> for the system's preferred face per script.
    ///     <para>
    ///         Run in parallel: each query is a process spawn costing ~10 ms, and eight of them in
    ///         sequence would be a tenth of a second added to every start. Together they cost about
    ///         one query. The whole thing is best-effort — no fontconfig, no fallbacks, no error.
    ///     </para>
    /// </summary>
    private static IEnumerable<string> FontconfigMatches()
    {
        try
        {
            var queries = Languages.Select(lang => Task.Run(() => Match(lang))).ToArray();
            // Bounded so a wedged fontconfig cannot hold up the whole application start.
            if (!Task.WaitAll(queries, TimeSpan.FromSeconds(2))) return [];

            return queries
                .Select(query => query.Result)
                .Where(path => path is not null && SafeExists(path))
                .Select(path => path!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string? Match(string language)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo {
                    FileName = "fc-match",
                    ArgumentList = {
                        "-f",
                        "%{file}",
                        $":lang={language}",
                    },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                }
            );

            if (process is null) return null;
            var path = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(2000);
            return path.Length > 0 ? path : null;
        }
        catch (Exception)
        {
            return null; // fc-match not installed, or not permitted to run
        }
    }

    /// <summary>
    ///     Load every fallback face that exists on this machine and register it with the engine.
    ///     Missing files are skipped in silence: a system without a CJK font is not a broken one,
    ///     it just cannot draw CJK, and there is nothing the app can do about it.
    /// </summary>
    public static void Register(ZigoteEngine engine)
    {
        var index = 0;
        foreach (var path in Candidates().Distinct(StringComparer.Ordinal))
        {
            if (!SafeExists(path)) continue;

            // Named by slot rather than by file, so the family name is stable and cannot collide
            // with a font the app registered itself.
            var family = $"fallback-{index}";
            if (!engine.LoadFont(family, path)) continue;
            engine.AddFallbackFont(family);
            index++;
        }

        Registered = index;
    }

    /// <summary>How many fallback faces were found on this machine. Zero means anything outside the
    ///     bundled font's coverage will render as boxes — worth surfacing in a diagnostics view.</summary>
    public static int Registered { get; private set; }

    /// <summary>A font directory can be a dangling symlink or on a mount that has gone away.</summary>
    private static bool SafeExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }
}