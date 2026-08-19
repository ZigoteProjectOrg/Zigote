using System.Buffers;

namespace Zigote.Cli;

/// <summary>A message meant for the user, not a stack trace.</summary>
public sealed class CliError(string message) : Exception(message);

/// <summary>
///     Input validation for everything that ends up inside a generated file. The names are not
///     merely cosmetic: they become directory names, C# namespaces, XML content and Android
///     package segments, so the rules here are what keeps a hostile or fat-fingered argument from
///     turning into path traversal, an unparseable csproj, or an APK that fails at install.
/// </summary>
public static class Identifier
{
    private static readonly SearchValues<char> NameChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_");

    /// <summary>
    ///     Windows still refuses these as file names regardless of extension, and a project a
    ///     teammate cannot clone onto their laptop is a trap worth refusing up front.
    /// </summary>
    private static readonly string[] ReservedNames = [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>A project name that is also a valid C# namespace and an Android package segment.</summary>
    public static string Validate(string name)
    {
        if (name.Length == 0) throw new CliError("the name cannot be empty");
        if (name.Length > 64)
            throw new CliError($"'{name[..24]}…' is too long — 64 characters at most.");
        if (!char.IsAsciiLetter(name[0])) throw new CliError($"'{name}' must start with a letter");
        if (name.AsSpan().ContainsAnyExcept(NameChars))
        {
            throw new CliError(
                $"'{name}' may only contain letters, digits and underscores — it becomes a C# namespace and an Android package segment."
            );
        }

        foreach (string reserved in ReservedNames)
        {
            if (name.Equals(value: reserved, comparisonType: StringComparison.OrdinalIgnoreCase))
                throw new CliError($"'{name}' is a reserved file name on Windows — pick another.");
        }

        return name;
    }

    /// <summary>
    ///     An Android application id: two or more dot-separated segments, each a plain identifier.
    ///     Validated rather than escaped, because the value lands in the manifest, the csproj AND
    ///     the installed package name — escaping would fix the XML and still ship a broken id.
    /// </summary>
    public static string ValidateAppId(string id)
    {
        if (id.Length is 0 or > 255) throw new CliError("--id must be 1–255 characters.");
        string[] segments = id.Split('.');
        bool wellFormed = segments.Length >= 2 && Array.TrueForAll(
            array: segments,
            match: static s => s.Length > 0
                               && char.IsAsciiLetter(s[0])
                               && !s.AsSpan().ContainsAnyExcept(NameChars)
        );
        if (!wellFormed)
        {
            throw new CliError(
                $"'{id}' is not a valid application id — use two or more dot-separated segments of letters, digits and underscores, each starting with a letter (e.g. dev.zigote.myapp)."
            );
        }

        return id;
    }

    /// <summary>
    ///     Escape a value for XML element content. The one generated value this guards is the
    ///     engine path, which is the one thing a user types that legitimately contains arbitrary
    ///     characters ("Projects &amp; Code") — everything else is identifier-validated instead.
    /// </summary>
    public static string XmlEscape(string value) =>
        System.Security.SecurityElement.Escape(value);
}

/// <summary>
///     Writes the generated files, refusing to clobber anything that already exists unless asked.
///     Collects what it did so the command can print one summary instead of a line per file.
/// </summary>
public sealed class Scaffolder
{
    private readonly bool _force;
    private readonly string _root;
    private readonly List<string> _skipped = [];
    private readonly List<string> _written = [];

    public Scaffolder(string root, bool force)
    {
        _root = Path.GetFullPath(root);
        _force = force;
    }

    public void Write(string relativePath, string content)
    {
        string path = Path.GetFullPath(Path.Combine(path1: _root, path2: relativePath));
        // Belt-and-braces containment: every relativePath today is CLI-authored and validated
        // upstream, but a future template mistake must fail here, not write outside the project.
        if (!path.StartsWith(value: _root + Path.DirectorySeparatorChar, comparisonType: StringComparison.Ordinal))
            throw new CliError($"refusing to write outside the project directory: '{relativePath}'");

        if (File.Exists(path) && !_force)
        {
            _skipped.Add(relativePath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Normalized to the platform's line endings so the generated sources do not show up as a
        // whole-file diff on the first commit from a different OS.
        File.WriteAllText(path: path, contents: content.ReplaceLineEndings());
        _written.Add(relativePath);
    }

    public void Report()
    {
        foreach (string f in _written) Console.WriteLine($"  created  {f}");
        foreach (string f in _skipped)
            Console.WriteLine($"  exists   {f}  (use --force to overwrite)");
    }
}
