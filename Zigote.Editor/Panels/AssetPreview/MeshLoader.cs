using System.Globalization;

namespace Zigote.Editor.Panels.AssetPreview;

/// <summary>
///     Pure-C# CPU mesh loader for asset thumbnails. Reads the engine's <c>.zmesh</c> binary cache,
///     plain-text <c>.obj</c> files, and falls back to merging a model's sibling
///     <c>.mesh_cache/</c> directory for opaque formats (glTF/GLB/FBX/…). Returns position + normal
///     geometry only — enough for a flat/Lambert thumbnail — and never throws (returns
///     <c>null</c> on any failure). It deliberately avoids the native renderer.
/// </summary>
public static class MeshLoader
{
    private const int MaxTriangles = 250_000;

    public static MeshData? Load(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return null;
            string ext = Path.GetExtension(path).ToLowerInvariant();

            var result = ext switch {
                ".zmesh" => LoadZMesh(path),
                ".obj" => LoadObj(path),
                _ => LoadFromMeshCache(path),
            };

            if (result is not { } m || m.TriangleCount == 0) return null;
            return Subsample(m);
        }
        catch
        {
            return null;
        }
    }

    // ── .zmesh ────────────────────────────────────────────────────────────────

    private static MeshData? LoadZMesh(string path)
    {
        using var fs = File.OpenRead(path);
        return ReadZMesh(fs);
    }

    private static MeshData? ReadZMesh(Stream stream)
    {
        using var br = new BinaryReader(stream);
        // Header: magic 'Z','M','S','H' | version | vertexCount | indexCount (all u32, LE).
        byte b0 = br.ReadByte();
        byte b1 = br.ReadByte();
        byte b2 = br.ReadByte();
        byte b3 = br.ReadByte();
        if (b0 != 0x5A || b1 != 0x4D || b2 != 0x53 || b3 != 0x48) return null; // not "ZMSH"

        uint version = br.ReadUInt32();
        // v1 = 48 B full-float Vertex (pos f32[3]@0, normal f32[3]@12, uv@24, tangent@32);
        // v2 = 28 B GpuVertex (pos f32[3]@0, normal snorm8[4]@12, uv@16, tangent snorm8[4]@24).
        if (version != 1 && version != 2) return null;
        uint vertexCount = br.ReadUInt32();
        uint indexCount = br.ReadUInt32();
        if (vertexCount == 0 || indexCount == 0) return null;
        if (vertexCount > 50_000_000 || indexCount > 200_000_000) return null; // sanity

        float[] positions = new float[vertexCount * 3];
        float[] normals = new float[vertexCount * 3];

        int stride = version == 1 ? 48 : 28;
        int vbytes = checked((int)(vertexCount * stride));
        byte[] buf = br.ReadBytes(vbytes);
        if (buf.Length < vbytes) return null;

        for (int i = 0; i < vertexCount; i++)
        {
            int o = i * stride;
            positions[(i * 3) + 0] = BitConverter.ToSingle(value: buf, startIndex: o + 0);
            positions[(i * 3) + 1] = BitConverter.ToSingle(value: buf, startIndex: o + 4);
            positions[(i * 3) + 2] = BitConverter.ToSingle(value: buf, startIndex: o + 8);
            if (version == 1)
            {
                normals[(i * 3) + 0] = BitConverter.ToSingle(value: buf, startIndex: o + 12);
                normals[(i * 3) + 1] = BitConverter.ToSingle(value: buf, startIndex: o + 16);
                normals[(i * 3) + 2] = BitConverter.ToSingle(value: buf, startIndex: o + 20);
            }
            else
            {
                // snorm8 → [-1, 1] (matches the GPU's hardware normalize on fetch).
                normals[(i * 3) + 0] = (sbyte)buf[o + 12] / 127f;
                normals[(i * 3) + 1] = (sbyte)buf[o + 13] / 127f;
                normals[(i * 3) + 2] = (sbyte)buf[o + 14] / 127f;
            }
        }

        int[] indices = new int[indexCount];
        int ibytes = checked((int)(indexCount * 4));
        byte[] ibuf = br.ReadBytes(ibytes);
        if (ibuf.Length < ibytes) return null;
        for (int i = 0; i < indexCount; i++)
        {
            uint idx = BitConverter.ToUInt32(value: ibuf, startIndex: i * 4);
            if (idx >= vertexCount) return null;
            indices[i] = (int)idx;
        }

        return new MeshData {
            Positions = positions,
            Normals = normals,
            Indices = indices,
        };
    }

    // ── .obj ──────────────────────────────────────────────────────────────────

    private static MeshData? LoadObj(string path)
    {
        var positions = new List<float>(); // v
        var normalsSrc = new List<float>(); // vn
        var outPos = new List<float>();
        var outNorm = new List<float>();
        var outIdx = new List<int>();
        bool hasAnyNormal = false;

        using var reader = new StreamReader(path);
        string? line;
        char[] sep = new[] {
            ' ',
            '\t',
        };

        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            if (line[0] == '#') continue;

            if (line.StartsWith(value: "v ", comparisonType: StringComparison.Ordinal))
            {
                string[] p = line.Split(
                    separator: sep,
                    options: StringSplitOptions.RemoveEmptyEntries
                );
                if (p.Length >= 4 &&
                    TryF(s: p[1], f: out float x) && TryF(s: p[2], f: out float y) &&
                    TryF(s: p[3], f: out float z))
                {
                    positions.Add(x);
                    positions.Add(y);
                    positions.Add(z);
                }
            }
            else if (line.StartsWith(value: "vn ", comparisonType: StringComparison.Ordinal))
            {
                string[] p = line.Split(
                    separator: sep,
                    options: StringSplitOptions.RemoveEmptyEntries
                );
                if (p.Length >= 4 &&
                    TryF(s: p[1], f: out float x) && TryF(s: p[2], f: out float y) &&
                    TryF(s: p[3], f: out float z))
                {
                    normalsSrc.Add(x);
                    normalsSrc.Add(y);
                    normalsSrc.Add(z);
                }
            }
            else if (line.StartsWith(value: "f ", comparisonType: StringComparison.Ordinal))
            {
                string[] p = line.Split(
                    separator: sep,
                    options: StringSplitOptions.RemoveEmptyEntries
                );
                // Build the polygon's vertex/normal indices, then fan-triangulate.
                var faceV = new List<int>(p.Length - 1);
                var faceN = new List<int>(p.Length - 1);
                for (int i = 1; i < p.Length; i++)
                {
                    if (!ParseObjVertex(
                            token: p[i],
                            vCount: positions.Count / 3,
                            nCount: normalsSrc.Count / 3,
                            vi: out int vi,
                            ni: out int ni
                        ))
                    {
                        faceV.Clear();
                        break;
                    }

                    faceV.Add(vi);
                    faceN.Add(ni);
                }

                if (faceV.Count < 3) continue;

                for (int i = 1; i + 1 < faceV.Count; i++)
                {
                    EmitObjVertex(
                        srcPos: positions,
                        srcNorm: normalsSrc,
                        vi: faceV[0],
                        ni: faceN[0],
                        outPos: outPos,
                        outNorm: outNorm,
                        outIdx: outIdx,
                        hasAnyNormal: ref hasAnyNormal
                    );
                    EmitObjVertex(
                        srcPos: positions,
                        srcNorm: normalsSrc,
                        vi: faceV[i],
                        ni: faceN[i],
                        outPos: outPos,
                        outNorm: outNorm,
                        outIdx: outIdx,
                        hasAnyNormal: ref hasAnyNormal
                    );
                    EmitObjVertex(
                        srcPos: positions,
                        srcNorm: normalsSrc,
                        vi: faceV[i + 1],
                        ni: faceN[i + 1],
                        outPos: outPos,
                        outNorm: outNorm,
                        outIdx: outIdx,
                        hasAnyNormal: ref hasAnyNormal
                    );
                }
            }
        }

        if (outIdx.Count < 3) return null;

        // If the file had no normals, leave them zero — the widget computes face normals.
        return new MeshData {
            Positions = outPos.ToArray(),
            Normals = outNorm.ToArray(),
            Indices = outIdx.ToArray(),
        };
    }

    private static void EmitObjVertex(List<float> srcPos, List<float> srcNorm, int vi, int ni,
        List<float> outPos, List<float> outNorm, List<int> outIdx, ref bool hasAnyNormal)
    {
        outIdx.Add(outPos.Count / 3);
        outPos.Add(srcPos[(vi * 3) + 0]);
        outPos.Add(srcPos[(vi * 3) + 1]);
        outPos.Add(srcPos[(vi * 3) + 2]);

        if (ni >= 0 && (ni * 3) + 2 < srcNorm.Count)
        {
            outNorm.Add(srcNorm[(ni * 3) + 0]);
            outNorm.Add(srcNorm[(ni * 3) + 1]);
            outNorm.Add(srcNorm[(ni * 3) + 2]);
            hasAnyNormal = true;
        }
        else
        {
            outNorm.Add(0f);
            outNorm.Add(0f);
            outNorm.Add(0f);
        }
    }

    /// <summary>Parse an OBJ face token (v, v/t, v//n, v/t/n; 1-based, negatives relative).</summary>
    private static bool ParseObjVertex(string token, int vCount, int nCount, out int vi, out int ni)
    {
        vi = -1;
        ni = -1;
        string[] parts = token.Split('/');
        if (parts.Length == 0) return false;

        if (!int.TryParse(s: parts[0], result: out int v)) return false;
        vi = v > 0 ? v - 1 : vCount + v; // negative = relative to end
        if (vi < 0 || vi >= vCount) return false;

        if (parts.Length >= 3 && parts[2].Length > 0 &&
            int.TryParse(s: parts[2], result: out int n))
            ni = n > 0 ? n - 1 : nCount + n;

        return true;
    }

    private static bool TryF(string s, out float f)
    {
        return float.TryParse(
            s: s,
            style: NumberStyles.Float,
            provider: CultureInfo.InvariantCulture,
            result: out f
        );
    }

    // ── .mesh_cache fallback (gltf/glb/fbx/dae/ply/stl) ─────────────────────────

    private static MeshData? LoadFromMeshCache(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return null;

        string cacheDir = Path.Combine(path1: dir, path2: ".mesh_cache");
        if (!Directory.Exists(cacheDir)) return null;

        string[] files = Directory.GetFiles(path: cacheDir, searchPattern: "*.zmesh");
        if (files.Length == 0) return null;

        var allPos = new List<float>();
        var allNorm = new List<float>();
        var allIdx = new List<int>();

        foreach (string f in files)
        {
            MeshData? part;
            try
            {
                part = LoadZMesh(f);
            }
            catch
            {
                part = null;
            }

            if (part is not { } m) continue;

            int baseVert = allPos.Count / 3;
            allPos.AddRange(m.Positions);
            allNorm.AddRange(m.Normals);
            foreach (int idx in m.Indices) allIdx.Add(idx + baseVert);

            // Don't merge an unbounded number of submeshes into a giant buffer.
            if (allIdx.Count / 3 > MaxTriangles) break;
        }

        if (allIdx.Count < 3) return null;
        return new MeshData {
            Positions = allPos.ToArray(),
            Normals = allNorm.ToArray(),
            Indices = allIdx.ToArray(),
        };
    }

    // ── Triangle cap ────────────────────────────────────────────────────────────

    private static MeshData Subsample(MeshData m)
    {
        int tris = m.TriangleCount;
        if (tris <= MaxTriangles) return m;

        // Keep every Nth triangle so the thumbnail stays representative but bounded.
        int stride = (tris + MaxTriangles - 1) / MaxTriangles; // ceil
        var kept = new List<int>(MaxTriangles * 3);
        for (int t = 0; t < tris; t += stride)
        {
            int b = t * 3;
            kept.Add(m.Indices[b + 0]);
            kept.Add(m.Indices[b + 1]);
            kept.Add(m.Indices[b + 2]);
        }

        return new MeshData {
            Positions = m.Positions,
            Normals = m.Normals,
            Indices = kept.ToArray(),
        };
    }

    /// <summary>Flat position/normal geometry. Positions and normals are xyz triples.</summary>
    public readonly struct MeshData
    {
        public required float[] Positions { get; init; } // length = VertexCount * 3
        public required float[] Normals { get; init; } // length = VertexCount * 3
        public required int[] Indices { get; init; } // triangle list

        public int VertexCount => Positions.Length / 3;
        public int TriangleCount => Indices.Length / 3;
    }
}
