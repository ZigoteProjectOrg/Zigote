namespace Zigote.Game.Resources;

public sealed class Mesh3D
{
    public Mesh3D() { }

    public Mesh3D(string name) => Name = name;

    public List<Primitive> Primitives { get; } = [];
    public string Name { get; set; } = "";

    // ── Built-in mesh factories ───────────────────────────────────────────────

    public static Mesh3D CreateCube()
    {
        var verts = new Vertex[] {
            // +X
            new(
                px: 0.5f,
                py: -0.5f,
                pz: -0.5f,
                nx: 1,
                ny: 0,
                nz: 0,
                u: 0,
                v: 1
            ),
            new(
                px: 0.5f,
                py: 0.5f,
                pz: -0.5f,
                nx: 1,
                ny: 0,
                nz: 0,
                u: 0,
                v: 0
            ),
            new(
                px: 0.5f,
                py: 0.5f,
                pz: 0.5f,
                nx: 1,
                ny: 0,
                nz: 0,
                u: 1,
                v: 0
            ),
            new(
                px: 0.5f,
                py: -0.5f,
                pz: 0.5f,
                nx: 1,
                ny: 0,
                nz: 0,
                u: 1,
                v: 1
            ),
            // -X
            new(
                px: -0.5f,
                py: -0.5f,
                pz: 0.5f,
                nx: -1,
                ny: 0,
                nz: 0,
                u: 0,
                v: 1
            ),
            new(
                px: -0.5f,
                py: 0.5f,
                pz: 0.5f,
                nx: -1,
                ny: 0,
                nz: 0,
                u: 0,
                v: 0
            ),
            new(
                px: -0.5f,
                py: 0.5f,
                pz: -0.5f,
                nx: -1,
                ny: 0,
                nz: 0,
                u: 1,
                v: 0
            ),
            new(
                px: -0.5f,
                py: -0.5f,
                pz: -0.5f,
                nx: -1,
                ny: 0,
                nz: 0,
                u: 1,
                v: 1
            ),
            // +Y
            new(
                px: -0.5f,
                py: 0.5f,
                pz: -0.5f,
                nx: 0,
                ny: 1,
                nz: 0,
                u: 0,
                v: 1
            ),
            new(
                px: -0.5f,
                py: 0.5f,
                pz: 0.5f,
                nx: 0,
                ny: 1,
                nz: 0,
                u: 0,
                v: 0
            ),
            new(
                px: 0.5f,
                py: 0.5f,
                pz: 0.5f,
                nx: 0,
                ny: 1,
                nz: 0,
                u: 1,
                v: 0
            ),
            new(
                px: 0.5f,
                py: 0.5f,
                pz: -0.5f,
                nx: 0,
                ny: 1,
                nz: 0,
                u: 1,
                v: 1
            ),
            // -Y
            new(
                px: -0.5f,
                py: -0.5f,
                pz: 0.5f,
                nx: 0,
                ny: -1,
                nz: 0,
                u: 0,
                v: 1
            ),
            new(
                px: -0.5f,
                py: -0.5f,
                pz: -0.5f,
                nx: 0,
                ny: -1,
                nz: 0,
                u: 0,
                v: 0
            ),
            new(
                px: 0.5f,
                py: -0.5f,
                pz: -0.5f,
                nx: 0,
                ny: -1,
                nz: 0,
                u: 1,
                v: 0
            ),
            new(
                px: 0.5f,
                py: -0.5f,
                pz: 0.5f,
                nx: 0,
                ny: -1,
                nz: 0,
                u: 1,
                v: 1
            ),
            // +Z
            new(
                px: 0.5f,
                py: -0.5f,
                pz: 0.5f,
                nx: 0,
                ny: 0,
                nz: 1,
                u: 0,
                v: 1
            ),
            new(
                px: 0.5f,
                py: 0.5f,
                pz: 0.5f,
                nx: 0,
                ny: 0,
                nz: 1,
                u: 0,
                v: 0
            ),
            new(
                px: -0.5f,
                py: 0.5f,
                pz: 0.5f,
                nx: 0,
                ny: 0,
                nz: 1,
                u: 1,
                v: 0
            ),
            new(
                px: -0.5f,
                py: -0.5f,
                pz: 0.5f,
                nx: 0,
                ny: 0,
                nz: 1,
                u: 1,
                v: 1
            ),
            // -Z
            new(
                px: -0.5f,
                py: -0.5f,
                pz: -0.5f,
                nx: 0,
                ny: 0,
                nz: -1,
                u: 0,
                v: 1
            ),
            new(
                px: -0.5f,
                py: 0.5f,
                pz: -0.5f,
                nx: 0,
                ny: 0,
                nz: -1,
                u: 0,
                v: 0
            ),
            new(
                px: 0.5f,
                py: 0.5f,
                pz: -0.5f,
                nx: 0,
                ny: 0,
                nz: -1,
                u: 1,
                v: 0
            ),
            new(
                px: 0.5f,
                py: -0.5f,
                pz: -0.5f,
                nx: 0,
                ny: 0,
                nz: -1,
                u: 1,
                v: 1
            ),
        };

        uint[] faceIdx = [0, 1, 2, 0, 2, 3];
        uint[] indices = new uint[6 * 6];
        for (int face = 0; face < 6; face++)
        for (int i = 0; i < 6; i++)
            indices[(face * 6) + i] = (uint)(face * 4) + faceIdx[i];

        var mesh = new Mesh3D("cube");
        mesh.Primitives.Add(new Primitive(vertices: verts, indices: indices));
        return mesh;
    }

    public static Mesh3D CreateQuad()
    {
        var verts = new Vertex[] {
            new(
                px: -0.5f,
                py: -0.5f,
                pz: 0f,
                nx: 0,
                ny: 0,
                nz: 1,
                u: 0,
                v: 1
            ),
            new(
                px: 0.5f,
                py: -0.5f,
                pz: 0f,
                nx: 0,
                ny: 0,
                nz: 1,
                u: 1,
                v: 1
            ),
            new(
                px: 0.5f,
                py: 0.5f,
                pz: 0f,
                nx: 0,
                ny: 0,
                nz: 1,
                u: 1,
                v: 0
            ),
            new(
                px: -0.5f,
                py: 0.5f,
                pz: 0f,
                nx: 0,
                ny: 0,
                nz: 1,
                u: 0,
                v: 0
            ),
        };
        uint[] indices = [0, 1, 2, 0, 2, 3];
        var mesh = new Mesh3D("quad");
        mesh.Primitives.Add(new Primitive(vertices: verts, indices: indices));
        return mesh;
    }

    public static Mesh3D CreateSphere(int rings = 16, int segments = 24)
    {
        int vertCount = (rings + 1) * (segments + 1);
        int idxCount = rings * segments * 6;
        var verts = new Vertex[vertCount];
        uint[] indices = new uint[idxCount];

        int vi = 0;
        for (int ring = 0; ring <= rings; ring++)
        {
            float phi = MathF.PI * ring / rings;
            for (int seg = 0; seg <= segments; seg++)
            {
                float theta = 2f * MathF.PI * seg / segments;
                float x = MathF.Sin(phi) * MathF.Cos(theta);
                float y = MathF.Cos(phi);
                float z = MathF.Sin(phi) * MathF.Sin(theta);
                verts[vi++] = new Vertex(
                    px: x * 0.5f,
                    py: y * 0.5f,
                    pz: z * 0.5f,
                    nx: x,
                    ny: y,
                    nz: z,
                    u: (float)seg / segments,
                    v: (float)ring / rings
                );
            }
        }

        int ii = 0;
        for (int ring = 0; ring < rings; ring++)
        for (int seg = 0; seg < segments; seg++)
        {
            uint a = (uint)((ring * (segments + 1)) + seg);
            uint b = a + 1;
            uint c = (uint)(((ring + 1) * (segments + 1)) + seg);
            uint d = c + 1;
            indices[ii++] = a;
            indices[ii++] = c;
            indices[ii++] = b;
            indices[ii++] = b;
            indices[ii++] = c;
            indices[ii++] = d;
        }

        var mesh = new Mesh3D("sphere");
        mesh.Primitives.Add(new Primitive(vertices: verts, indices: indices));
        return mesh;
    }
}
