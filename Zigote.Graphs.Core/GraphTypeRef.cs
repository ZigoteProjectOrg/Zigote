namespace Zigote.Graphs.Core;

/// <summary>
///     Opaque type identifier. Core stores type IDs; domains register what they mean.
/// </summary>
public readonly record struct GraphTypeRef(string Id)
{
    // ── Built-in core types ───────────────────────────────────────────────────
    public static readonly GraphTypeRef Bool = new("core.bool");
    public static readonly GraphTypeRef Int = new("core.int");
    public static readonly GraphTypeRef Float = new("core.float");
    public static readonly GraphTypeRef Float2 = new("core.float2");
    public static readonly GraphTypeRef Float3 = new("core.float3");
    public static readonly GraphTypeRef Float4 = new("core.float4");
    public static readonly GraphTypeRef String = new("core.string");
    public static readonly GraphTypeRef Color = new("core.color");
    public static readonly GraphTypeRef Any = new("core.any");

    public override string ToString()
    {
        return Id;
    }
}