using Zigote.Core.Math3D;

namespace Zigote.Game.Resources;

public sealed class Primitive
{
    public Primitive() { }

    public Primitive(Vertex[] vertices, uint[] indices, int? materialIndex = null)
    {
        Vertices = vertices;
        Indices = indices;
        MaterialIndex = materialIndex;
    }

    public Vertex[] Vertices { get; set; } = [];
    public uint[] Indices { get; set; } = [];
    public int? MaterialIndex { get; set; }

    /// <summary>Recalculates vertex normals from triangle faces (overwrites existing normals).</summary>
    public void RecalculateNormals()
    {
        for (int i = 0; i < Vertices.Length; i++)
            Vertices[i].NX = Vertices[i].NY = Vertices[i].NZ = 0f;

        for (int i = 0; i + 2 < Indices.Length; i += 3)
        {
            int ia = (int)Indices[i];
            int ib = (int)Indices[i + 1];
            int ic = (int)Indices[i + 2];

            var a = new Vec3(x: Vertices[ia].PX, y: Vertices[ia].PY, z: Vertices[ia].PZ);
            var b = new Vec3(x: Vertices[ib].PX, y: Vertices[ib].PY, z: Vertices[ib].PZ);
            var c = new Vec3(x: Vertices[ic].PX, y: Vertices[ic].PY, z: Vertices[ic].PZ);
            var n = (b - a).Cross(c - a).Normalize();

            foreach (int idx in (int[])[ia, ib, ic])
            {
                Vertices[idx].NX += n.X;
                Vertices[idx].NY += n.Y;
                Vertices[idx].NZ += n.Z;
            }
        }

        for (int i = 0; i < Vertices.Length; i++)
        {
            var nn = new Vec3(x: Vertices[i].NX, y: Vertices[i].NY, z: Vertices[i].NZ).Normalize();
            Vertices[i].NX = nn.X;
            Vertices[i].NY = nn.Y;
            Vertices[i].NZ = nn.Z;
        }
    }
}
