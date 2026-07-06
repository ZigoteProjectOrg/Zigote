using Zigote.Core.Math3D;

namespace Zigote.Render2D;

/// <summary>Flattens a <see cref="Mat4" /> into caller-owned scratch (no Mat4.ToArray alloc).</summary>
internal static class Mat4Marshal
{
    public static void WriteColumnMajor(in Mat4 m, Span<float> dst)
    {
        dst[0] = m.Col0.X;
        dst[1] = m.Col0.Y;
        dst[2] = m.Col0.Z;
        dst[3] = m.Col0.W;
        dst[4] = m.Col1.X;
        dst[5] = m.Col1.Y;
        dst[6] = m.Col1.Z;
        dst[7] = m.Col1.W;
        dst[8] = m.Col2.X;
        dst[9] = m.Col2.Y;
        dst[10] = m.Col2.Z;
        dst[11] = m.Col2.W;
        dst[12] = m.Col3.X;
        dst[13] = m.Col3.Y;
        dst[14] = m.Col3.Z;
        dst[15] = m.Col3.W;
    }
}