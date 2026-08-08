using Zigote.Core.Math3D;

namespace Zigote.Render2D;

/// <summary>One sprite draw request; consumed by <see cref="Renderer2D.Draw(in SpriteDraw)" />.</summary>
public struct SpriteDraw
{
    public float X, Y, Z; // world position of the PIVOT point
    public float Rotation; // radians CCW
    public float Width, Height; // world size
    public float PivotX, PivotY; // 0..1 inside the sprite rect; (0.5, 0.5) = center
    public SpriteFrame Frame;
    public Vec4 Color; // tint, straight alpha (1,1,1,1 = untinted)
    public bool FlipX, FlipY;

    /// <summary>
    ///     Corner radius in world units (the same space as <see cref="Width" />/
    ///     <see cref="Height" />). 0 = square corners. A radius at or above half the shorter side
    ///     gives a capsule — and on a square quad, a circle.
    /// </summary>
    public float CornerRadius;

    /// <summary>
    ///     Outline thickness in world units. 0 fills the quad; greater than 0 draws only a ring that
    ///     thick inside the edge, in <see cref="Color" /> — a stroked rect rather than a filled one.
    /// </summary>
    public float BorderWidth;

    public short SortingLayer;
    public short OrderInLayer;
    public uint Texture;
    public Material2D? Material; // null → Material2D.Default
}