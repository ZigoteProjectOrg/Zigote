using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace Zigote.Tests;

/// <summary>
///     Asserts every C# FFI mirror against the layout the Zig compiler actually produced, read from
///     <c>Zigote.Engine/zig-out/ffi-manifest.json</c> (written by <c>zig build ffi-manifest</c>).
///     <para>
///         <see cref="AbiLayoutTests" /> pins the same layout against hardcoded literals, which is a
///         weaker guarantee than it looks: the Zig-side <c>comptime @offsetOf</c> asserts catch a
///         drift on that side, and <c>RendererAbiInfo.Validate</c> catches a change in TOTAL size at
///         startup — but a field <em>reorder</em> that preserves total size passes both, and every
///         field past the change is then silently misread at full speed. Nothing compared the two
///         layouts field by field. This does.
///     </para>
/// </summary>
public class AbiManifestTests
{
    private static readonly string? ManifestPath = FindManifest();

    private static string? FindManifest()
    {
        // Walk up from the test binary to the repo root, then into the engine's build output.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Zigote.Engine", "zig-out", "ffi-manifest.json");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>Zig's snake_case field name to the C# PascalCase mirror.</summary>
    private static string ToPascal(string snake)
    {
        var sb = new System.Text.StringBuilder(snake.Length);
        bool upper = true;
        foreach (char c in snake)
        {
            if (c == '_') { upper = true; continue; }

            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }

        return sb.ToString();
    }

    private static Type? FindMirror(string zigName)
    {
        // ZgGlyphRunQuad is mirrored as ZgGlyphRunQuad; everything else matches by name.
        string[] candidates = zigName == "ZgGlyphRunQuad"
            ? ["ZgGlyphRunQuad", zigName]
            : [zigName];
        var asm = typeof(Zigote.Core.Native.ZgEvent).Assembly;
        foreach (string name in candidates)
        {
            var t = asm.GetType("Zigote.Core.Native." + name);
            if (t is not null) return t;
        }

        return null;
    }

    [Fact]
    public void EveryMirroredStruct_MatchesTheZigLayoutExactly()
    {
        Assert.SkipWhen(
            ManifestPath is null,
            "ffi-manifest.json not found — run `zig build ffi-manifest` in Zigote.Engine."
        );

        using var doc = JsonDocument.Parse(File.ReadAllText(ManifestPath!));
        var types = doc.RootElement.GetProperty("types");

        int checkedStructs = 0, checkedFields = 0;
        foreach (var t in types.EnumerateArray())
        {
            if (t.GetProperty("kind").GetString() != "struct") continue;

            string zigName = t.GetProperty("name").GetString()!;
            var mirror = FindMirror(zigName);
            // Not every wire struct is mirrored on the C# side (ZgSize, ZgResult are used only
            // through other APIs). Skipping is correct; silently skipping ALL of them is not, so
            // the count is asserted at the end.
            if (mirror is null) continue;

            checkedStructs++;
            int zigSize = t.GetProperty("size").GetInt32();
            Assert.Equal(zigSize, Marshal.SizeOf(mirror));

            foreach (var f in t.GetProperty("fields").EnumerateArray())
            {
                string zigField = f.GetProperty("name").GetString()!;
                int zigOffset = f.GetProperty("offset").GetInt32();

                // Padding fields exist to pin layout and are not mirrored.
                if (zigField.StartsWith("pad", StringComparison.Ordinal)) continue;

                var member = mirror.GetField(
                    ToPascal(zigField),
                    BindingFlags.Public | BindingFlags.Instance
                );

                // A C# mirror may deliberately alias a Zig field under another name (the paint
                // command reuses `radius` as an image U0, and so on). Those still have to sit at
                // the SAME offset, so an unmatched name is only acceptable if some field does.
                if (member is null)
                {
                    bool someFieldAtOffset = mirror
                        .GetFields(BindingFlags.Public | BindingFlags.Instance)
                        .Any(x => (int)Marshal.OffsetOf(mirror, x.Name) == zigOffset);
                    Assert.True(
                        someFieldAtOffset,
                        $"{zigName}.{zigField} (offset {zigOffset}) has no C# field at that offset"
                    );
                    continue;
                }

                checkedFields++;
                Assert.Equal(zigOffset, (int)Marshal.OffsetOf(mirror, member.Name));
            }
        }

        // Guard against the whole thing passing vacuously (a renamed namespace, an empty manifest).
        Assert.True(checkedStructs >= 6, $"only {checkedStructs} structs were checked");
        Assert.True(checkedFields >= 80, $"only {checkedFields} fields were checked");
    }
}
