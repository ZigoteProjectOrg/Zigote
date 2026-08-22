using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Zigote.Generators;

[Generator]
public class ZigoteBindingGenerator : IIncrementalGenerator
{
    // Entry points that get [SuppressGCTransition]: the runtime skips the cooperative→preemptive GC
    // transition (the dominant per-call P/Invoke cost) for these. ONLY safe for tiny, non-blocking,
    // leaf calls that write a few fields and return immediately — no GPU submit/present, no file IO,
    // no event polling, no managed callback, no unbounded copy. These per-node/per-frame setters
    // qualify; render/submit/poll/load entry points deliberately do NOT (they can block, and
    // suppressing the transition would stall the GC for the whole call).
    private static readonly HashSet<string> SuppressGcTransition = new(StringComparer.Ordinal) {
        "zigote_scene_update_node",
        "zigote_scene_set_node_visible",
        "zigote_scene_set_mesh_color",
        "zigote_scene_set_mesh_roughness",
        "zigote_scene_set_mesh_surface",
        "zigote_scene_set_mesh_emissive",
        "zigote_scene_set_mesh_effect",
        "zigote_scene_set_mesh_alpha_mode",
        "zigote_scene_set_mesh_double_sided",
        "zigote_scene_set_mesh_volume",
        "zigote_scene_set_mesh_occlusion_strength",
        "zigote_scene_set_light_properties",
        "zigote_scene_set_selected_node",
        "zigote_render_set_frustum_cull",
    };

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Ingest every `src/ffi/*.zig` file, not just root.zig, so each subsystem's `export fn`
        // ABI can live alongside its implementation (ecs.zig / physics.zig / audio.zig / …).
        var zigFiles = context.AdditionalTextsProvider
            .Where(static file =>
                {
                    string p = file.Path.Replace(oldChar: '\\', newChar: '/');
                    // src/ffi/*.zig carries the exports; src/abi.zig carries the enums those
                    // exports return, which must be generated rather than hand-mirrored.
                    return p.EndsWith(".zig") && (p.Contains("/ffi/") || p.EndsWith("/abi.zig"));
                }
            )
            .Select(static (file, cancellationToken) =>
                {
                    var text = file.GetText(cancellationToken);
                    return (Path: file.Path.Replace(oldChar: '\\', newChar: '/'),
                        Text: text?.ToString() ?? string.Empty);
                }
            )
            .Collect();

        context.RegisterSourceOutput(
            source: zigFiles,
            action: static (spc, files) =>
            {
                // Deterministic order (path-sorted) so the generated output is stable across builds.
                var ordered = files
                    .Where(static f => !string.IsNullOrEmpty(f.Text))
                    .OrderBy(keySelector: static f => f.Path, comparer: StringComparer.Ordinal);

                string? source = GenerateBindings(ordered);
                if (source is null) return;
                spc.AddSource(
                    hintName: "NativeEngine.g.cs",
                    sourceText: SourceText.From(text: source, encoding: Encoding.UTF8)
                );
            }
        );
    }

    private class ZigEnum
    {
        public string Name = string.Empty;
        public string BackingType = "int";
        public List<KeyValuePair<string, string>> Members = new List<KeyValuePair<string, string>>();
    }

    /// <summary>
    ///     Parses <c>pub const Name = enum(i32) { a = 0, b = -1, ... };</c> out of a Zig source.
    ///     Only explicitly-valued members are emitted, which is every enum that crosses the ABI —
    ///     an implicit value would make the wire encoding depend on declaration order.
    /// </summary>
    private static IEnumerable<ZigEnum> ParseZigEnums(string text)
    {
        const string decl = "pub const ";
        int at = 0;
        while (true)
        {
            int found = text.IndexOf(value: decl, startIndex: at, comparisonType: StringComparison.Ordinal);
            if (found < 0) yield break;

            at = found + decl.Length;
            if (found != 0 && text[found - 1] != '\n') continue; // must start a line

            int eq = text.IndexOf(value: " = enum(", startIndex: at, comparisonType: StringComparison.Ordinal);
            int lineEnd = text.IndexOf(value: '\n', startIndex: at);
            if (eq < 0 || (lineEnd >= 0 && eq > lineEnd)) continue;

            string name = text.Substring(startIndex: at, length: eq - at).Trim();
            int backingStart = eq + " = enum(".Length;
            int backingEnd = text.IndexOf(value: ')', startIndex: backingStart);
            if (backingEnd < 0) continue;

            string zigBacking = text.Substring(startIndex: backingStart, length: backingEnd - backingStart).Trim();
            string backing = zigBacking switch
            {
                "i32" => "int", "u32" => "uint", "i16" => "short", "u16" => "ushort",
                "i8" => "sbyte", "u8" => "byte", "i64" => "long", "u64" => "ulong",
                _ => "int",
            };

            int open = text.IndexOf(value: '{', startIndex: backingEnd);
            if (open < 0) continue;

            // Stop at the first `}` at the start of a line — the enum's own closing brace. Member
            // functions inside the enum are skipped by the "must contain =" test below.
            int close = text.IndexOf(value: "\n};", startIndex: open);
            if (close < 0) continue;

            var members = new List<KeyValuePair<string, string>>();
            foreach (string rawLine in text.Substring(startIndex: open + 1, length: close - open - 1)
                         .Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
                if (!line.EndsWith(",", StringComparison.Ordinal)) continue;

                int assign = line.IndexOf('=');
                if (assign < 0) continue;
                // `0 => .sine,` is a switch arm inside a member function, not a member.
                if (line.Contains("=>")) continue;

                string memberName = line.Substring(startIndex: 0, length: assign).Trim();
                string value = line.Substring(startIndex: assign + 1).TrimEnd(',').Trim();
                if (memberName.Length == 0 || memberName.Contains(" ")) continue;

                members.Add(new KeyValuePair<string, string>(key: SnakeToPascal(memberName), value: value));
            }

            if (members.Count > 0)
            {
                yield return new ZigEnum { Name = name, BackingType = backing, Members = members };
            }
        }
    }

    private static int SizeOfCsType(string t) => t switch
    {
        "byte" => 1, "sbyte" => 1,
        "ushort" => 2, "short" => 2,
        "uint" => 4, "int" => 4, "float" => 4,
        "ulong" => 8, "long" => 8, "double" => 8, "byte*" => 8,
        _ => 4,
    };

    private class ZigStructField
    {
        public string Name = string.Empty;
        public string Type = string.Empty;
        public int ArrayLength; // 0 = not an array
    }

    private class ZigStruct
    {
        public string Name = string.Empty;
        public List<ZigStructField> Fields = new List<ZigStructField>();
    }

    /// <summary>
    ///     Maps a Zig field type to its C# equivalent. Deliberately a closed set: every type that
    ///     actually crosses the ABI today. An unknown type returns null and the whole struct is
    ///     skipped rather than emitted wrong — a silently mistyped field is the failure this
    ///     generator exists to prevent.
    /// </summary>
    private static string? MapZigFieldType(string zigType, out int arrayLength)
    {
        arrayLength = 0;
        string t = zigType.Trim();

        // A nested wire struct — the scene ops all begin with a ZgSceneOpHeader. Passed through by
        // name; its size and alignment come from the struct already parsed above it in abi.zig.
        if (t.StartsWith("Zg", StringComparison.Ordinal) && t.All(c => char.IsLetterOrDigit(c))) return t;

        if (t.StartsWith("[*c]", StringComparison.Ordinal) || t.StartsWith("[*]", StringComparison.Ordinal))
            return "byte*"; // every pointer field the ABI has is a byte pointer

        if (t.StartsWith("[", StringComparison.Ordinal))
        {
            int close = t.IndexOf(']');
            if (close > 1 && int.TryParse(t.Substring(startIndex: 1, length: close - 1), out int len))
            {
                string elem = t.Substring(close + 1).Trim();
                // Fixed-size arrays become C# `fixed` buffers, which only accept blittable
                // primitives — u8 and f32 are the two the wire contract uses.
                if (elem == "u8" || elem == "f32")
                {
                    arrayLength = len;
                    return elem == "u8" ? "byte" : "float";
                }
            }

            return null;
        }

        return t switch
        {
            "u8" => "byte", "u16" => "ushort", "u32" => "uint", "u64" => "ulong",
            "i8" => "sbyte", "i16" => "short", "i32" => "int", "i64" => "long",
            "f32" => "float", "f64" => "double", "bool" => "byte",
            _ => null,
        };
    }

    /// <summary>Parses <c>pub const Name = extern struct { field: type, ... };</c>.</summary>
    private static IEnumerable<ZigStruct> ParseZigStructs(string text)
    {
        const string decl = "pub const ";
        int at = 0;
        while (true)
        {
            int found = text.IndexOf(value: decl, startIndex: at, comparisonType: StringComparison.Ordinal);
            if (found < 0) yield break;

            at = found + decl.Length;
            if (found != 0 && text[found - 1] != '\n') continue;

            int marker = text.IndexOf(value: " = extern struct {", startIndex: at, comparisonType: StringComparison.Ordinal);
            int lineEnd = text.IndexOf(value: '\n', startIndex: at);
            if (marker < 0 || (lineEnd >= 0 && marker > lineEnd)) continue;

            string name = text.Substring(startIndex: at, length: marker - at).Trim();
            int open = marker + " = extern struct ".Length;
            int close = text.IndexOf(value: "\n};", startIndex: open);
            if (close < 0) continue;

            var st = new ZigStruct { Name = name };
            bool ok = true;
            foreach (string rawLine in text.Substring(startIndex: open + 1, length: close - open - 1).Split('\n'))
            {
                string line = rawLine.Trim();
                int comment = line.IndexOf("//", StringComparison.Ordinal);
                if (comment >= 0) line = line.Substring(startIndex: 0, length: comment).Trim();
                if (line.Length == 0 || !line.EndsWith(",", StringComparison.Ordinal)) continue;

                line = line.TrimEnd(',');
                int colon = line.IndexOf(':');
                if (colon < 0) continue;

                string fieldName = line.Substring(startIndex: 0, length: colon).Trim();
                string fieldType = line.Substring(colon + 1).Trim();
                // Drop a default value: `pad: [2]u8 = .{0} ** 2`.
                int eq = fieldType.IndexOf('=');
                if (eq >= 0) fieldType = fieldType.Substring(startIndex: 0, length: eq).Trim();

                string? mapped = MapZigFieldType(zigType: fieldType, arrayLength: out int arrayLen);
                if (mapped is null) { ok = false; break; }

                st.Fields.Add(new ZigStructField { Name = fieldName, Type = mapped, ArrayLength = arrayLen });
            }

            if (ok && st.Fields.Count > 0) yield return st;
        }
    }

    private static string? GenerateBindings(IEnumerable<(string Path, string Text)> files)
    {
        // Concatenate exports across files, first-wins dedup by entry point (an entry point must be
        // unique in the shared lib, so a duplicate would only ever be an authoring mistake).
        var functions = new List<ZigFunction>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string _, string text) in files)
        foreach (var fn in ParseZigFunctions(text))
        {
            if (seen.Add(fn.EntryPoint))
                functions.Add(fn);
        }

        var structs = new List<ZigStruct>();
        var seenStructs = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string path, string text) in files)
        {
            if (!path.EndsWith("/abi.zig", StringComparison.Ordinal)) continue;

            foreach (var st in ParseZigStructs(text))
            {
                if (seenStructs.Add(st.Name))
                    structs.Add(st);
            }
        }

        // Enums come from src/abi.zig ONLY — that file is the wire contract. Implementation files
        // carry enums that never cross the ABI (audio waveform kinds and the like), and those have
        // member functions whose `switch` arms are not enum members.
        var enums = new List<ZigEnum>();
        var seenEnums = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string path, string text) in files)
        {
            if (!path.EndsWith("/abi.zig", StringComparison.Ordinal)) continue;

            foreach (var en in ParseZigEnums(text))
            {
                if (seenEnums.Add(en.Name))
                    enums.Add(en);
            }
        }

        if (functions.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Generated from src/ffi/*.zig — do not edit.");
        sb.AppendLine("// [DllImport] is used here intentionally: [LibraryImport] partial methods");
        sb.AppendLine(
            "// require the SDK LibraryImportGenerator which cannot process generator output."
        );
        sb.AppendLine("using System.Runtime.InteropServices;");
        sb.AppendLine();
        sb.AppendLine("namespace Zigote.Core.Native;");
        sb.AppendLine();

        // Enums the exports return or take. Generated from the Zig declaration so the values
        // cannot drift from the contract the way a hand-copied mirror can.
        foreach (var e in enums)
        {
            sb.AppendLine($"/// <summary>Generated from Zig <c>{e.Name}</c>.</summary>");
            sb.AppendLine($"public enum {e.Name} : {e.BackingType}");
            sb.AppendLine("{");
            foreach (var member in e.Members)
                sb.AppendLine($"    {member.Key} = {member.Value},");

            sb.AppendLine("}");
            sb.AppendLine();
        }

        // Wire structs, generated from the Zig declaration. Each is `partial`: the canonical
        // fields and their offsets come from here, and ZgStructs.cs adds only the ALIAS fields —
        // the C# names for slots the Zig side deliberately reuses per command kind (a paint
        // command's `radius` is also an image's u0 and a shader id). Offsets are computed the same
        // way Zig lays the struct out, and AbiManifestTests checks the result against what the
        // compiler actually produced.
        var nestedSizes = new Dictionary<string, int>(StringComparer.Ordinal);
        var nestedAligns = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var st in structs)
        {
            int offset = 0, maxAlign = 1;
            var placed = new List<(string Name, string Type, int Offset, int ArrayLength)>();
            foreach (var f in st.Fields)
            {
                int size = nestedSizes.TryGetValue(f.Type, out int ns) ? ns : SizeOfCsType(f.Type);
                int align = nestedAligns.TryGetValue(f.Type, out int na) ? na : size;
                int count = f.ArrayLength == 0 ? 1 : f.ArrayLength;
                if (f.ArrayLength > 0) align = 1; // byte arrays
                if (align > maxAlign) maxAlign = align;
                if (offset % align != 0) offset += align - offset % align;
                placed.Add((f.Name, f.Type, offset, f.ArrayLength));
                offset += size * count;
            }

            if (offset % maxAlign != 0) offset += maxAlign - offset % maxAlign;
            nestedSizes[st.Name] = offset;
            nestedAligns[st.Name] = maxAlign;

            sb.AppendLine($"/// <summary>Generated from Zig <c>{st.Name}</c>. Aliases live in ZgStructs.cs.</summary>");
            sb.AppendLine($"[StructLayout(LayoutKind.Explicit, Size = {offset})]");
            sb.AppendLine($"public unsafe partial struct {st.Name}");
            sb.AppendLine("{");
            foreach (var f in placed)
            {
                string csName = SnakeToPascal(f.Name);
                if (f.ArrayLength > 0)
                    sb.AppendLine($"    [FieldOffset({f.Offset})] public fixed {f.Type} {csName}[{f.ArrayLength}];");
                else
                    sb.AppendLine($"    [FieldOffset({f.Offset})] public {f.Type} {csName};");
            }

            sb.AppendLine("}");
            sb.AppendLine();
        }

        sb.AppendLine(
            "#pragma warning disable SYSLIB1054 // Use LibraryImportAttribute instead of DllImportAttribute"
        );
        sb.AppendLine("internal static unsafe partial class NativeEngine");
        sb.AppendLine("{");

        foreach (var func in functions)
        {
            sb.AppendLine($"    [DllImport(Lib, EntryPoint = \"{func.EntryPoint}\")]");
            if (SuppressGcTransition.Contains(func.EntryPoint))
                sb.AppendLine("    [SuppressGCTransition]");
            if (func.ReturnType == "bool")
                sb.AppendLine("    [return: MarshalAs(UnmanagedType.U1)]");

            sb.Append($"    {func.Visibility} static extern {func.ReturnType} {func.Name}(");

            for (int i = 0; i < func.Parameters.Count; i++)
            {
                var param = func.Parameters[i];
                sb.Append($"{param.Type} {param.Name}");
                if (i < func.Parameters.Count - 1) sb.Append(", ");
            }

            sb.AppendLine(");");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        sb.AppendLine("#pragma warning restore SYSLIB1054");
        return sb.ToString();
    }

    private static List<ZigFunction> ParseZigFunctions(string source)
    {
        var functions = new List<ZigFunction>();
        int index = 0;

        while (true)
        {
            index = source.IndexOf(value: "export fn ", startIndex: index);
            if (index == -1) break;

            // A real declaration is always top-level, hence always at column 0. Anything indented is
            // the phrase appearing inside something else — a doc comment, or a string literal in a
            // test that greps these very declarations — and must not emit a binding. Matching on the
            // column covers every such case at once; matching on `//` covered only the first.
            if (source.LastIndexOf(value: '\n', startIndex: index) + 1 != index)
            {
                index += "export fn ".Length;
                continue;
            }

            index += "export fn ".Length;

            int openParen = source.IndexOf(value: "(", startIndex: index);
            if (openParen == -1) break;

            string fnName = source.Substring(startIndex: index, length: openParen - index).Trim();

            int depth = 1;
            int closeParen = -1;
            for (int i = openParen + 1; i < source.Length; i++)
            {
                if (source[i] == '(')
                    depth++;
                else if (source[i] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        closeParen = i;
                        break;
                    }
                }
            }

            if (closeParen == -1) break;

            string paramsStr = source.Substring(
                startIndex: openParen + 1,
                length: closeParen - openParen - 1
            );

            int nextBrace = source.IndexOf(value: "{", startIndex: closeParen);
            if (nextBrace == -1) break;

            string returnTypeStr = source.Substring(
                startIndex: closeParen + 1,
                length: nextBrace - closeParen - 1
            ).Trim();

            var func = new ZigFunction();
            func.EntryPoint = fnName;

            string stripped = fnName.StartsWith("zigote_") ? fnName.Substring(7) : fnName;
            func.Name = SnakeToPascal(stripped);
            func.Visibility = "public";

            func.ReturnType = MapZigTypeToCSharp(paramName: "", zigType: returnTypeStr);

            var paramSegments = SplitParameters(paramsStr);
            foreach (string segment in paramSegments)
            {
                int colonIdx = segment.IndexOf(':');
                if (colonIdx == -1) continue;

                string paramName = segment.Substring(startIndex: 0, length: colonIdx).Trim();
                string paramType = segment.Substring(colonIdx + 1).Trim();

                string csharpName = SnakeToCamel(paramName);
                string csharpType = MapZigTypeToCSharp(paramName: paramName, zigType: paramType);

                func.Parameters.Add(
                    new ZigParameter {
                        Name = csharpName,
                        Type = csharpType,
                    }
                );
            }

            functions.Add(func);
            index = nextBrace + 1;
        }

        return functions;
    }

    private static List<string> SplitParameters(string paramsStr)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        int depth = 0;
        for (int i = 0; i < paramsStr.Length; i++)
        {
            char c = paramsStr[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;

            if (c == ',' && depth == 0)
            {
                string p = sb.ToString().Trim();
                if (!string.IsNullOrEmpty(p)) list.Add(p);
                sb.Clear();
            }
            else
                sb.Append(c);
        }

        string last = sb.ToString().Trim();
        if (!string.IsNullOrEmpty(last)) list.Add(last);
        return list;
    }

    private static string MapZigTypeToCSharp(string paramName, string zigType)
    {
        zigType = zigType.Trim();

        if (zigType.Contains("fn") && zigType.Contains("callconv"))
            return MapFnPointer(zigType);

        if (zigType.StartsWith("[*c]const ") || zigType.StartsWith("[*]const "))
        {
            string innerType = zigType.Substring(zigType.IndexOf("const ") + 6).Trim();
            return MapZigTypeToCSharp(paramName: "", zigType: innerType) + "*";
        }

        if (zigType.StartsWith("[*c]") || zigType.StartsWith("[*]"))
        {
            string innerType = zigType.Substring(zigType.IndexOf("]") + 1).Trim();
            return MapZigTypeToCSharp(paramName: "", zigType: innerType) + "*";
        }

        if (zigType.StartsWith("*"))
        {
            string innerType = zigType.Substring(1).Trim();
            string csharpInnerType = MapSingleZigTypeToCSharp(innerType);
            if (paramName.StartsWith("out_")) return "out " + csharpInnerType;
            return csharpInnerType + "*";
        }

        return MapSingleZigTypeToCSharp(zigType);
    }

    /// <summary>
    ///     Map a Zig C-callconv function-pointer type (e.g.
    ///     <c>
    ///         ?*const fn (u32, u32, u32) callconv(.c)
    ///         void
    ///     </c>
    ///     ) to a C# unmanaged function pointer <c>delegate* unmanaged[Cdecl]&lt;…&gt;</c>, parsing
    ///     the real parameter and return types rather than assuming a fixed shape.
    /// </summary>
    private static string MapFnPointer(string zigType)
    {
        int fnIdx = zigType.IndexOf(value: "fn", comparisonType: StringComparison.Ordinal);
        int open = fnIdx >= 0 ? zigType.IndexOf(value: '(', startIndex: fnIdx) : -1;
        if (open < 0) return "delegate* unmanaged[Cdecl]<int, byte*, void>"; // defensive fallback

        int close = MatchingParen(s: zigType, open: open);
        string paramsStr = zigType.Substring(startIndex: open + 1, length: close - open - 1);

        // Return type follows the callconv(...) clause.
        string retType = "void";
        int ccIdx = zigType.IndexOf(
            value: "callconv",
            startIndex: close,
            comparisonType: StringComparison.Ordinal
        );
        if (ccIdx >= 0)
        {
            int ccOpen = zigType.IndexOf(value: '(', startIndex: ccIdx);
            int ccClose = MatchingParen(s: zigType, open: ccOpen);
            retType = zigType.Substring(ccClose + 1).Trim();
        }

        var args = new List<string>();
        foreach (string p in SplitParameters(paramsStr))
        {
            // A parameter may be "name: type" or a bare "type".
            string t = p;
            int colon = p.IndexOf(':');
            if (colon >= 0) t = p.Substring(colon + 1).Trim();
            args.Add(MapZigTypeToCSharp(paramName: "", zigType: t));
        }

        args.Add(retType is "void" or "" ? "void" : MapSingleZigTypeToCSharp(retType));
        return "delegate* unmanaged[Cdecl]<" + string.Join(separator: ", ", values: args) + ">";
    }

    /// <summary>Index of the ')' matching the '(' at <paramref name="open" /> (nesting-aware).</summary>
    private static int MatchingParen(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == '(')
                depth++;
            else if (s[i] == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
        }

        return s.Length - 1;
    }

    private static string MapSingleZigTypeToCSharp(string zigType)
    {
        return zigType switch {
            "u8" => "byte",
            "u16" => "ushort",
            "u32" => "uint",
            "u64" => "ulong",
            "i32" => "int",
            "c_int" => "int",
            "f32" => "float",
            "usize" => "nuint",
            "bool" => "bool",
            "void" => "void",
            "ZgResult" => "ZgResult",
            _ => zigType,
        };
    }

    private static string SnakeToCamel(string snake)
    {
        if (string.IsNullOrEmpty(snake)) return snake;
        string[] parts = snake.Split('_');
        var sb = new StringBuilder();
        sb.Append(parts[0]);
        for (int i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
            {
                sb.Append(char.ToUpperInvariant(parts[i][0]));
                sb.Append(parts[i].Substring(1));
            }
        }

        return sb.ToString();
    }

    private static string SnakeToPascal(string snake)
    {
        if (string.IsNullOrEmpty(snake)) return snake;
        string[] parts = snake.Split('_');
        var sb = new StringBuilder();
        foreach (string part in parts)
        {
            if (part.Length > 0)
            {
                if (part.Equals(value: "3d", comparisonType: StringComparison.OrdinalIgnoreCase))
                    sb.Append("3D");
                else
                {
                    sb.Append(char.ToUpperInvariant(part[0]));
                    sb.Append(part.Substring(1));
                }
            }
        }

        return sb.ToString();
    }

    private class ZigFunction
    {
        public string EntryPoint { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ReturnType { get; set; } = string.Empty;
        public string Visibility { get; set; } = "public";
        public List<ZigParameter> Parameters { get; } = [];
    }

    private class ZigParameter
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
    }
}
