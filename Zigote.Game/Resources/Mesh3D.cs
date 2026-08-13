namespace Zigote.Game.Resources;

public sealed class Mesh3D
{
    public Mesh3D()
    {
    }

    public Mesh3D(string name)
    {
        Name = name;
    }

    public List<Primitive> Primitives { get; } = [];
    public string Name { get; set; } = "";

    // ── Built-in mesh factories ───────────────────────────────────────────────

    public static Mesh3D CreateCube()
    {
        var verts = new Vertex[] {
            // +X
            new(
                0.5f,
                -0.5f,
                -0.5f,
                1,
                0,
                0,
                0,
                1
            ),
            new(
                0.5f,
                0.5f,
                -0.5f,
                1,
                0,
                0,
                0,
                0
            ),
            new(
                0.5f,
                0.5f,
                0.5f,
                1,
                0,
                0,
                1,
                0
            ),
            new(
                0.5f,
                -0.5f,
                0.5f,
                1,
                0,
                0,
                1,
                1
            ),
            // -X
            new(
                -0.5f,
                -0.5f,
                0.5f,
                -1,
                0,
                0,
                0,
                1
            ),
            new(
                -0.5f,
                0.5f,
                0.5f,
                -1,
                0,
                0,
                0,
                0
            ),
            new(
                -0.5f,
                0.5f,
                -0.5f,
                -1,
                0,
                0,
                1,
                0
            ),
            new(
                -0.5f,
                -0.5f,
                -0.5f,
                -1,
                0,
                0,
                1,
                1
            ),
            // +Y
            new(
                -0.5f,
                0.5f,
                -0.5f,
                0,
                1,
                0,
                0,
                1
            ),
            new(
                -0.5f,
                0.5f,
                0.5f,
                0,
                1,
                0,
                0,
                0
            ),
            new(
                0.5f,
                0.5f,
                0.5f,
                0,
                1,
                0,
                1,
                0
            ),
            new(
                0.5f,
                0.5f,
                -0.5f,
                0,
                1,
                0,
                1,
                1
            ),
            // -Y
            new(
                -0.5f,
                -0.5f,
                0.5f,
                0,
                -1,
                0,
                0,
                1
            ),
            new(
                -0.5f,
                -0.5f,
                -0.5f,
                0,
                -1,
                0,
                0,
                0
            ),
            new(
                0.5f,
                -0.5f,
                -0.5f,
                0,
                -1,
                0,
                1,
                0
            ),
            new(
                0.5f,
                -0.5f,
                0.5f,
                0,
                -1,
                0,
                1,
                1
            ),
            // +Z
            new(
                0.5f,
                -0.5f,
                0.5f,
                0,
                0,
                1,
                0,
                1
            ),
            new(
                0.5f,
                0.5f,
                0.5f,
                0,
                0,
                1,
                0,
                0
            ),
            new(
                -0.5f,
                0.5f,
                0.5f,
                0,
                0,
                1,
                1,
                0
            ),
            new(
                -0.5f,
                -0.5f,
                0.5f,
                0,
                0,
                1,
                1,
                1
            ),
            // -Z
            new(
                -0.5f,
                -0.5f,
                -0.5f,
                0,
                0,
                -1,
                0,
                1
            ),
            new(
                -0.5f,
                0.5f,
                -0.5f,
                0,
                0,
                -1,
                0,
                0
            ),
            new(
                0.5f,
                0.5f,
                -0.5f,
                0,
                0,
                -1,
                1,
                0
            ),
            new(
                0.5f,
                -0.5f,
                -0.5f,
                0,
                0,
                -1,
                1,
                1
            ),
        };

        uint[] faceIdx = [0, 1, 2, 0, 2, 3];
        var indices = new uint[6 * 6];
        for (var face = 0; face < 6; face++)
        for (var i = 0; i < 6; i++)
            indices[face * 6 + i] = (uint)(face * 4) + faceIdx[i];

        var mesh = new Mesh3D("cube");
        mesh.Primitives.Add(new Primitive(verts, indices));
        return mesh;
    }

    public static Mesh3D CreateQuad()
    {
        var verts = new Vertex[] {
            new(
                -0.5f,
                -0.5f,
                0f,
                0,
                0,
                1,
                0,
                1
            ),
            new(
                0.5f,
                -0.5f,
                0f,
                0,
                0,
                1,
                1,
                1
            ),
            new(
                0.5f,
                0.5f,
                0f,
                0,
                0,
                1,
                1,
                0
            ),
            new(
                -0.5f,
                0.5f,
                0f,
                0,
                0,
                1,
                0,
                0
            ),
        };
        uint[] indices = [0, 1, 2, 0, 2, 3];
        var mesh = new Mesh3D("quad");
        mesh.Primitives.Add(new Primitive(verts, indices));
        return mesh;
    }

    public static Mesh3D CreateSphere(int rings = 16, int segments = 24)
    {
        var vertCount = (rings + 1) * (segments + 1);
        var idxCount = rings * segments * 6;
        var verts = new Vertex[vertCount];
        var indices = new uint[idxCount];

        var vi = 0;
        for (var ring = 0; ring <= rings; ring++)
        {
            var phi = MathF.PI * ring / rings;
            for (var seg = 0; seg <= segments; seg++)
            {
                var theta = 2f * MathF.PI * seg / segments;
                var x = MathF.Sin(phi) * MathF.Cos(theta);
                var y = MathF.Cos(phi);
                var z = MathF.Sin(phi) * MathF.Sin(theta);
                verts[vi++] = new Vertex(
                    x * 0.5f,
                    y * 0.5f,
                    z * 0.5f,
                    x,
                    y,
                    z,
                    (float)seg / segments,
                    (float)ring / rings
                );
            }
        }

        var ii = 0;
        for (var ring = 0; ring < rings; ring++)
        for (var seg = 0; seg < segments; seg++)
        {
            var a = (uint)(ring * (segments + 1) + seg);
            var b = a + 1;
            var c = (uint)((ring + 1) * (segments + 1) + seg);
            var d = c + 1;
            indices[ii++] = a;
            indices[ii++] = c;
            indices[ii++] = b;
            indices[ii++] = b;
            indices[ii++] = c;
            indices[ii++] = d;
        }

        var mesh = new Mesh3D("sphere");
        mesh.Primitives.Add(new Primitive(verts, indices));
        return mesh;
    }
}
