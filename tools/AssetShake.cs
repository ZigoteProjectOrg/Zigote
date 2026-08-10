// AssetShake — publish-time asset tree shaking for Zigote apps.
//
// Deletes the files under an app's deployed asset roots (Assets/, Fonts/) that the app's code cannot
// reach, and derives the Unicode ranges its text fonts actually need. Both answers come from one
// source: the string literals compiled into the app's assemblies.
//
// WHY IL AND NOT SOURCE. The literals live in the `#US` (user strings) metadata heap of every managed
// assembly, so the scan needs no source access, sees generated code for free (the .arb → GalleryL10n
// catalogues are literals like any other, which is why the localized Cyrillic coverage falls out
// without anyone listing it), and reads identically for a JIT and an AOT publish.
//
// WHAT IT CANNOT SEE. A path composed at runtime — $"Assets/{name}.png" — is two literals ("Assets/"
// and ".png") that name no file, so the file it opens looks unreachable. That is the whole soundness
// boundary of this tool, and the reason the MSBuild side is opt-in per app with a --keep escape hatch
// and a report that names every dropped file. Nothing is ever dropped quietly.
//
// Matching is deliberately loose — relative path, file name OR stem, exact or as a substring of any
// literal — because every ambiguity should keep a file. Stems are not optional: Zigote.UI builds its
// font paths as `faceName + ".ttf"`, so "Inter-Medium" is the only literal that exists for that file.
//
// This is a .NET 10 file-based app — no project, no dependencies. Run it directly:
//
//   dotnet run tools/AssetShake.cs -- <publishDir> [options]
//
// Options:
//   --assemblies <list>    ';'-separated assemblies to scan (the app's own + its Zigote.* closure).
//                          Scan the INTERMEDIATE assemblies, not the publish output: an AOT publish
//                          has no managed assemblies left in the bundle to read.
//   --roots <list>         ';'-separated asset roots relative to publishDir (default: Assets;Fonts)
//   --keep <list>          ';'-separated glob patterns (matched on the root-relative path) to force-keep
//   --extra-unicodes <s>   Extra ranges to union into the derived set, "U+0400-04FF,U+2190" form
//   --unicodes-out <path>  Write the derived range list here for the build to read back
//   --dry-run              Report what would be dropped; delete nothing
//
// Exit codes: 0 = done (even if nothing was dropped), 1 = bad usage / unreadable publish dir.

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Enumeration;
using System.Reflection.PortableExecutable;
using System.Text;

// Build-log output is parsed by people and diffed by CI, not read in the developer's locale — pin the
// culture so "0.84 MB" doesn't come out as "0,84 MB" on one machine and not another.
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var positional = new List<string>();
var options = new Dictionary<string, string>(StringComparer.Ordinal);
var dryRun = false;

for (var i = 0; i < args.Length; i++)
{
    var arg = args[i];
    if (arg == "--dry-run") { dryRun = true; continue; }
    if (arg.StartsWith("--", StringComparison.Ordinal))
    {
        if (i + 1 >= args.Length) { Console.Error.WriteLine($"AssetShake: {arg} needs a value"); return 1; }
        options[arg] = args[++i];
        continue;
    }

    positional.Add(arg);
}

if (positional.Count != 1)
{
    Console.Error.WriteLine("AssetShake: usage: AssetShake <publishDir> [options]");
    return 1;
}

var publishDir = Path.GetFullPath(positional[0]);
if (!Directory.Exists(publishDir))
{
    Console.Error.WriteLine($"AssetShake: publish directory not found: {publishDir}");
    return 1;
}

var assemblies = Split(Opt("--assemblies")).ToList();
var roots = Split(Opt("--roots")).ToList();
if (roots.Count == 0) roots = ["Assets", "Fonts"];
var keepPatterns = Split(Opt("--keep")).ToList();

// ── 1. Everything the code can name ───────────────────────────────────────────────────────────────
var literals = new HashSet<string>(StringComparer.Ordinal);
var scanned = 0;
var unreadable = new List<string>();
foreach (var assembly in assemblies)
{
    if (!File.Exists(assembly)) continue; // a reference that didn't resolve is not this tool's problem
    try
    {
        if (ReadUserStrings(assembly, literals)) scanned++;
        else unreadable.Add($"{Path.GetFileName(assembly)} (no managed metadata)");
    }
    catch (Exception e)
    {
        unreadable.Add($"{Path.GetFileName(assembly)} ({e.Message})");
    }
}

// An assembly that could not be read is NOT a warning. Its literals are missing from the reachable
// set, so every asset only it referenced now looks dead and gets deleted — a silently wrong publish,
// in the one direction that matters. This is not hypothetical: NativeAOT swaps the app's own managed
// assembly out of @(IntermediateAssembly) for the native binary, and the first AOT run of this tool
// deleted the app's assets because it "scanned" a Mach-O executable and found no strings in it.
if (unreadable.Count > 0)
{
    Console.Error.WriteLine(
        "AssetShake: could not read " + string.Join(", ", unreadable) +
        " — refusing to shake, because assets referenced only there would be deleted as unreachable."
    );
    return 1;
}

if (scanned == 0)
{
    // No literals means everything looks unreachable, which would empty the asset roots. Refuse.
    Console.Error.WriteLine(
        "AssetShake: no assemblies could be scanned — refusing to shake (everything would look unreachable)."
    );
    return 1;
}

// ── 2. Which asset files survive ──────────────────────────────────────────────────────────────────
var candidates = new List<string>();
foreach (var root in roots)
{
    var dir = Path.Combine(publishDir, root);
    if (Directory.Exists(dir))
        candidates.AddRange(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories));
}

if (candidates.Count == 0)
{
    Console.WriteLine("Asset shake: no asset roots in the publish output — nothing to do.");
    WriteUnicodes();
    return 0;
}

var kept = new List<string>();
var dropped = new List<string>();
var licenses = new List<string>();

foreach (var file in candidates.OrderBy(f => f, StringComparer.Ordinal))
{
    var relative = Path.GetRelativePath(publishDir, file).Replace('\\', '/');
    var name = Path.GetFileName(file);

    // Licences are decided in a second pass: whether one must ship depends on whether the font it
    // covers survived, which is not known until every other file has been classified.
    if (name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase)) { licenses.Add(file); continue; }

    if (IsReachable(relative, name) || IsForceKept(relative)) kept.Add(file);
    else dropped.Add(file);
}

// A bundled font's licence is only obligatory while the font itself ships (OFL 1.1 §2 attaches to
// redistribution of the font file). Keep the licence whose subject survived, drop the one whose
// subject just did — a LICENSE-NotoEmoji-OFL.txt next to no Noto Emoji covers nothing.
foreach (var licence in licenses)
{
    var subject = LicenceSubject(Path.GetFileName(licence));
    var subjectShips = subject is null || kept.Any(k =>
        Path.GetFileName(k).StartsWith(subject, StringComparison.OrdinalIgnoreCase));

    if (subjectShips || IsForceKept(Path.GetRelativePath(publishDir, licence).Replace('\\', '/')))
        kept.Add(licence);
    else
        dropped.Add(licence);
}

// ── 3. Report, then act ───────────────────────────────────────────────────────────────────────────
long freed = 0;
foreach (var file in dropped)
{
    var size = new FileInfo(file).Length;
    freed += size;
    Console.WriteLine(
        $"  {(dryRun ? "would drop" : "drop")} {Path.GetRelativePath(publishDir, file).Replace('\\', '/')}" +
        $" ({size / 1024.0:F0} KB) — unreachable"
    );
    if (!dryRun) File.Delete(file);
}

if (!dryRun)
    foreach (var root in roots)
        PruneEmptyDirectories(Path.Combine(publishDir, root));

Console.WriteLine(
    $"Asset shake: kept {kept.Count}, dropped {dropped.Count} file(s), " +
    $"{freed / (1024.0 * 1024.0):F2} MB freed (from {literals.Count} literals in {scanned} assemblies)" +
    (dryRun ? " [dry run]" : "") + "."
);

WriteUnicodes();
return 0;

// ── helpers ───────────────────────────────────────────────────────────────────────────────────────

string? Opt(string key)
{
    return options.TryGetValue(key, out var value) ? value : null;
}

static IEnumerable<string> Split(string? value)
{
    return (value ?? "").Split(
        [';', ','],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
    );
}

// Reachable = some literal names this file. Exact match on the root-relative path, the file name or
// the stem covers direct opens; the substring pass covers the Path.Combine/concatenation forms.
bool IsReachable(string relative, string name)
{
    var stem = Path.GetFileNameWithoutExtension(name);
    if (literals.Contains(relative) || literals.Contains(name) || literals.Contains(stem)) return true;

    foreach (var literal in literals)
        if (literal.Contains(name, StringComparison.Ordinal) ||
            literal.Contains(stem, StringComparison.Ordinal) ||
            literal.Contains(relative, StringComparison.Ordinal))
            return true;

    return false;
}

bool IsForceKept(string relative)
{
    foreach (var pattern in keepPatterns)
        if (FileSystemName.MatchesSimpleExpression(pattern, relative, ignoreCase: true))
            return true;

    return false;
}

// "LICENSE-NotoEmoji-OFL.txt" → "NotoEmoji". A licence that doesn't name its subject (a bare
// LICENSE.txt) returns null and is always kept — guessing at what it covers is not this tool's job.
static string? LicenceSubject(string fileName)
{
    var parts = Path.GetFileNameWithoutExtension(fileName).Split('-');
    return parts.Length >= 2 ? parts[1] : null;
}

static void PruneEmptyDirectories(string dir)
{
    if (!Directory.Exists(dir)) return;
    foreach (var child in Directory.EnumerateDirectories(dir)) PruneEmptyDirectories(child);
    if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
}

// The subset ranges the surviving text fonts need: every codepoint the code can emit, plus a floor of
// U+0000-00FF. The floor is not padding — the engine's glyph router (freetype_text.zig routeFor)
// returns the primary face for anything below U+0100 WITHOUT consulting the system fallbacks, so a
// Latin-1 codepoint missing from the subset renders as tofu instead of falling back.
void WriteUnicodes()
{
    var outPath = Opt("--unicodes-out");
    if (outPath is null) return;

    var codepoints = new HashSet<int>();
    for (var c = 0x0000; c <= 0x00FF; c++) codepoints.Add(c);
    foreach (var literal in literals)
        foreach (var rune in literal.EnumerateRunes())
            codepoints.Add(rune.Value);

    foreach (var extra in Split(Opt("--extra-unicodes")))
    {
        var span = extra.Replace("U+", "", StringComparison.OrdinalIgnoreCase);
        var dash = span.IndexOf('-');
        if (dash < 0)
        {
            if (int.TryParse(span, System.Globalization.NumberStyles.HexNumber, null, out var single))
                codepoints.Add(single);
            continue;
        }

        if (int.TryParse(span[..dash], System.Globalization.NumberStyles.HexNumber, null, out var lo) &&
            int.TryParse(span[(dash + 1)..], System.Globalization.NumberStyles.HexNumber, null, out var hi))
            for (var c = lo; c <= hi && c - lo < 0x10000; c++)
                codepoints.Add(c);
    }

    // Collapse to contiguous runs — a subsetter takes the list on its command line, and one entry per
    // codepoint would be tens of thousands of arguments for an app with CJK strings.
    var sorted = codepoints.Order().ToList();
    var ranges = new List<string>();
    for (var i = 0; i < sorted.Count;)
    {
        var start = sorted[i];
        var end = start;
        while (i + 1 < sorted.Count && sorted[i + 1] == end + 1) { end = sorted[++i]; }
        i++;
        ranges.Add(start == end ? $"U+{start:X4}" : $"U+{start:X4}-{end:X4}");
    }

    File.WriteAllText(outPath, string.Join(",", ranges));
    Console.WriteLine($"Asset shake: derived {codepoints.Count} codepoints in {ranges.Count} range(s) for font subsetting.");
}

// Walk the `#US` heap of one assembly. MetadataReader can resolve a UserStringHandle but exposes no
// way to enumerate the heap, so the stream is located from the metadata root and read directly
// (ECMA-335 II.24.2.1 for the root, II.24.2.4 for the blob encoding) — the same hand-parsing the icon
// generator does for a font's cmap.
static bool ReadUserStrings(string assemblyPath, HashSet<string> into)
{
    using var stream = File.OpenRead(assemblyPath);
    using var pe = new PEReader(stream);
    if (!pe.HasMetadata) return false;

    var metadata = pe.GetMetadata().GetContent().AsSpan();
    if (metadata.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(metadata) != 0x424A5342)
        return false; // not a "BSJB" metadata root

    var position = 12;
    var versionLength = BinaryPrimitives.ReadInt32LittleEndian(metadata[position..]);
    position += 4 + Align4(versionLength);
    position += 2; // flags
    var streamCount = BinaryPrimitives.ReadUInt16LittleEndian(metadata[position..]);
    position += 2;

    for (var i = 0; i < streamCount && position + 8 < metadata.Length; i++)
    {
        var offset = BinaryPrimitives.ReadInt32LittleEndian(metadata[position..]);
        var size = BinaryPrimitives.ReadInt32LittleEndian(metadata[(position + 4)..]);
        position += 8;

        var nameStart = position;
        while (position < metadata.Length && metadata[position] != 0) position++;
        var name = Encoding.ASCII.GetString(metadata[nameStart..position]);
        position = nameStart + Align4(position - nameStart + 1); // name is null-terminated, 4-byte padded

        if (name != "#US" || offset < 0 || size < 0 || offset + size > metadata.Length) continue;

        var heap = metadata.Slice(offset, size);
        var at = 0;
        while (at < heap.Length)
        {
            var length = ReadCompressedUInt(heap, ref at);
            if (length < 0 || at + length > heap.Length) break;
            // A blob is (length-1) bytes of UTF-16 plus one trailing flag byte; length 0 is the empty
            // string the heap always starts with.
            if (length > 1) into.Add(Encoding.Unicode.GetString(heap.Slice(at, (length - 1) / 2 * 2)));
            at += length;
        }

        return true;
    }

    return true; // managed, but with no #US heap — an assembly with no string literals is legitimate

    static int Align4(int value)
    {
        return (value + 3) & ~3;
    }
}

// ECMA-335 II.23.2 compressed unsigned integer: 1, 2 or 4 bytes selected by the top bits.
static int ReadCompressedUInt(ReadOnlySpan<byte> data, ref int position)
{
    if (position >= data.Length) return -1;

    var first = data[position];
    if ((first & 0x80) == 0)
    {
        position += 1;
        return first;
    }

    if ((first & 0xC0) == 0x80)
    {
        if (position + 1 >= data.Length) return -1;
        var value = ((first & 0x3F) << 8) | data[position + 1];
        position += 2;
        return value;
    }

    if ((first & 0xE0) == 0xC0)
    {
        if (position + 3 >= data.Length) return -1;
        var value = ((first & 0x1F) << 24) | (data[position + 1] << 16) |
                    (data[position + 2] << 8) | data[position + 3];
        position += 4;
        return value;
    }

    return -1;
}
