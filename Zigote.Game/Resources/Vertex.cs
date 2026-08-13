using System.Runtime.InteropServices;

namespace Zigote.Game.Resources;

/// <summary>GPU vertex layout matching the Zig <c>Vertex</c> extern struct.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Vertex(
    float px,
    float py,
    float pz,
    float nx,
    float ny,
    float nz,
    float u,
    float v,
    float tx = 0,
    float ty = 0,
    float tz = 0,
    float tw = 1)
{
    public float PX = px, PY = py, PZ = pz; // position
    public float NX = nx, NY = ny, NZ = nz; // normal
    public float U = u, V = v; // texcoord 0
    public float TX = tx, TY = ty, TZ = tz, TW = tw; // tangent (w = handedness)
}
