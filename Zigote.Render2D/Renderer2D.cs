using Zigote.Core.Math3D;

namespace Zigote.Render2D;

/// <summary>
///     Frame-scoped sprite batcher: record draws between <see cref="Begin" /> and <see cref="End" />;
///     End stable-sorts by (SortingLayer, OrderInLayer, submission order), packs 14-float instances,
///     and submits one device call per (texture, material) run. Grow-only buffers — a steady-state
///     frame allocates zero bytes. Handles are not validated here (that's device/native-side).
/// </summary>
public sealed class Renderer2D
{
    // pos.xyz, rot, size.xy, uv0.xy, uv1.xy, rgba, corner_radius, border_width — must match
    // SpriteSystem.INSTANCE_FLOATS and the sprite shader's VsIn.
    private const int FloatsPerInstance = 16;

    private readonly ISpriteDevice _device;
    private readonly float[] _overlayVp = new float[16];
    private readonly float[] _sceneVp = new float[16];

    private SpriteDraw[] _draws = new SpriteDraw[64];
    private int[] _indices = new int[64];
    private float[] _instances = new float[64 * FloatsPerInstance];
    private ulong[] _keys = new ulong[64];

    public Renderer2D(ISpriteDevice device)
    {
        _device = device;
    }

    /// <summary>Draws recorded since <see cref="Begin" />.</summary>
    public int DrawCount { get; private set; }

    /// <summary>Batches emitted by the last <see cref="End" />.</summary>
    public int BatchCount { get; private set; }

    public void Begin(in Mat4 sceneViewProjection, in Mat4 overlayViewProjection, float viewportW,
        float viewportH)
    {
        Mat4Marshal.WriteColumnMajor(sceneViewProjection, _sceneVp);
        Mat4Marshal.WriteColumnMajor(overlayViewProjection, _overlayVp);
        DrawCount = 0;
        _device.Begin(
            _sceneVp,
            _overlayVp,
            viewportW,
            viewportH
        );
    }

    public void Draw(in SpriteDraw draw)
    {
        if (DrawCount == _draws.Length)
        {
            var grown = new SpriteDraw[_draws.Length * 2];
            Array.Copy(_draws, grown, DrawCount);
            _draws = grown;
        }

        _draws[DrawCount++] = draw;
    }

    public void Draw(SpriteTexture texture, Vec2 position, Vec2 size)
    {
        Draw(
            texture,
            position,
            size,
            texture.FullFrame,
            Vec4.One
        );
    }

    public void Draw(SpriteTexture texture, Vec2 position, Vec2 size, in SpriteFrame frame,
        Vec4 color, float rotation = 0f, short layer = 0, short order = 0)
    {
        var draw = new SpriteDraw {
            X = position.X,
            Y = position.Y,
            Z = 0f,
            Rotation = rotation,
            Width = size.X,
            Height = size.Y,
            PivotX = 0.5f,
            PivotY = 0.5f,
            Frame = frame,
            Color = color,
            SortingLayer = layer,
            OrderInLayer = order,
            Texture = texture.Handle,
            Material = null,
        };
        Draw(in draw);
    }

    public void End()
    {
        BatchCount = 0;
        var count = DrawCount;
        if (count == 0) return;

        if (_keys.Length < count)
        {
            _keys = new ulong[_draws.Length];
            _indices = new int[_draws.Length];
        }

        if (_instances.Length < count * FloatsPerInstance)
            _instances = new float[_draws.Length * FloatsPerInstance];

        // Unique keys (the low 32 bits are the submission sequence), so the sort is stable by
        // construction even though Array.Sort itself is introsort.
        for (var i = 0; i < count; i++)
        {
            ref readonly var d = ref _draws[i];
            _keys[i] = ((ulong)(ushort)(d.SortingLayer + 32768) << 48)
                       | ((ulong)(ushort)(d.OrderInLayer + 32768) << 32)
                       | (uint)i;
            _indices[i] = i;
        }

        Array.Sort(
            _keys,
            _indices,
            0,
            count
        );

        // A material's scalar fields are keyed by its reference (shared instance ⇒ identical
        // fields), so (texture, material reference) is the complete batch key.
        ref readonly var first = ref _draws[_indices[0]];
        var batchTexture = first.Texture;
        var batchMaterial = first.Material ?? Material2D.Default;
        var batchStart = 0;
        Pack(in first, _instances, 0);

        for (var s = 1; s < count; s++)
        {
            ref readonly var d = ref _draws[_indices[s]];
            var material = d.Material ?? Material2D.Default;
            if (d.Texture != batchTexture || !ReferenceEquals(material, batchMaterial))
            {
                Emit(
                    batchTexture,
                    batchMaterial,
                    batchStart,
                    s - batchStart
                );
                batchTexture = d.Texture;
                batchMaterial = material;
                batchStart = s;
            }

            Pack(in d, _instances, s * FloatsPerInstance);
        }

        Emit(
            batchTexture,
            batchMaterial,
            batchStart,
            count - batchStart
        );
    }

    private void Emit(uint texture, Material2D material, int start, int instanceCount)
    {
        _device.Submit(
            texture,
            material.Texture2,
            material.ShaderHandle,
            material.Blend,
            material.Stage,
            material.Params,
            new ReadOnlySpan<float>(
                _instances,
                start * FloatsPerInstance,
                instanceCount * FloatsPerInstance
            ),
            instanceCount
        );
        BatchCount++;
    }

    private static void Pack(in SpriteDraw d, float[] dst, int o)
    {
        // Bake the pivot into the position: the shader expands a center-pivot quad, so shift the
        // center by the rotated pivot→center offset.
        var ox = (0.5f - d.PivotX) * d.Width;
        var oy = (0.5f - d.PivotY) * d.Height;
        var cos = MathF.Cos(d.Rotation);
        var sin = MathF.Sin(d.Rotation);
        dst[o + 0] = d.X + ox * cos - oy * sin;
        dst[o + 1] = d.Y + ox * sin + oy * cos;
        dst[o + 2] = d.Z;
        dst[o + 3] = d.Rotation;
        dst[o + 4] = d.Width;
        dst[o + 5] = d.Height;

        var u0 = d.Frame.U0;
        var u1 = d.Frame.U1;
        var v0 = d.Frame.V0;
        var v1 = d.Frame.V1;
        if (d.FlipX) (u0, u1) = (u1, u0);
        if (d.FlipY) (v0, v1) = (v1, v0);
        dst[o + 6] = u0;
        dst[o + 7] = v0;
        dst[o + 8] = u1;
        dst[o + 9] = v1;

        dst[o + 10] = d.Color.X;
        dst[o + 11] = d.Color.Y;
        dst[o + 12] = d.Color.Z;
        dst[o + 13] = d.Color.W;

        dst[o + 14] = d.CornerRadius;
        dst[o + 15] = d.BorderWidth;
    }
}