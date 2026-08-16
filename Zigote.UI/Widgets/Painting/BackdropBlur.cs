using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Core.Paint;
using Zigote.UI.Host;

namespace Zigote.UI.Widgets;

/// <summary>
///     A gaussian blur of whatever has already been painted inside a box — the engine's
///     backdrop-sampling mechanism with a shader of its own on it.
///     <para>
///         The op is one quad: the renderer treats any backdrop-sampling command as a barrier,
///         copies the scene so far into a texture, and hands that texture to this shader. So the
///         rule for a caller is simply <i>paint the thing, then blur the box it is in</i> —
///         <see cref="Paint" /> from a widget's own <c>Paint</c>, after the content to be blurred.
///         Nothing is allocated, nothing has to be kept alive between frames, and the cost is
///         <see cref="Taps" /> texture reads per covered pixel.
///     </para>
/// </summary>
/// <remarks>
///     The engine also has a separable blur — two compute passes over a render texture, reached
///     with <c>PushRenderTexture</c> + <c>AddBlur</c> — which is cheaper per pixel than a gather.
///     But a gather over the backdrop needs no GPU resource owned by a widget: nothing to size,
///     destroy, or lose on a device change. At a decorative wash the cost is not the difference;
///     reach for the compute path if a blur ever lands somewhere hot.
/// </remarks>
public static class BackdropBlur
{
    /// <summary>
    ///     Taps in the gather. A golden-angle spiral with area-uniform radii and gaussian weights —
    ///     enough that an enlarged image behind the blur has no structure left to show through.
    /// </summary>
    private const int Taps = 24;

    /// <summary>
    ///     Our id in the engine's shader table. The table is shared with whatever an app registers
    ///     through <see cref="ZigoteEngine.RegisterShader" />, and a collision silently replaces
    ///     one shader with the other — apps must pick something else.
    /// </summary>
    public const uint ShaderId = 0x7A_A2_7B_10;

    private static bool _tried;
    private static bool _ok;

    /// <summary>
    ///     Whether the pipeline is compiled and <see cref="Paint" /> will emit. Callers that draw
    ///     something <i>only worth drawing blurred</i> check this first; a plain wash can just call
    ///     <see cref="Paint" /> and accept the sharp fallback.
    /// </summary>
    public static bool Ready
    {
        get
        {
            // First paint, not construction: registering compiles a pipeline, so it needs a device,
            // and a widget can be built before there is one.
            if (_tried) return _ok;
            if (App.Active is null) return false;

            // Once, whatever the answer: a pipeline that would not compile will not compile on the
            // next frame either, and asking again would be asking sixty times a second.
            _tried = true;
            _ok = ZigoteEngine.RegisterShader(id: ShaderId, wgsl: Source);
            return _ok;
        }
    }

    /// <summary>
    ///     Blur everything painted so far inside <paramref name="bounds" />. A no-op when the
    ///     pipeline is unavailable (no engine, or a driver that refused it) — the content stays
    ///     sharp, which is a worse backdrop but still a backdrop.
    /// </summary>
    /// <param name="bounds">The box to blur, in the caller's layout space (logical points).</param>
    /// <param name="reach">Blur radius in logical points.</param>
    /// <param name="cornerRadius">
    ///     Corner radius of the box, so a blur under a rounded panel does not square off its
    ///     corners. The shader must round the corner itself: a custom shader gets one bind group —
    ///     the backdrop — and cannot see the frame's clip stack.
    /// </param>
    /// <param name="density">
    ///     Device pixel ratio, from <c>MediaQuery.Of(...).DevicePixelRatio</c>. The parameters go
    ///     to the GPU in device pixels — a fragment shader's coordinates are physical — and without
    ///     the conversion a HiDPI screen gets a blur of half the reach over three-quarters of the
    ///     box.
    /// </param>
    public static void Paint(
        PaintList paint, Rect bounds, float reach, float cornerRadius, float density)
    {
        if (reach <= 0f || !Ready) return;

        // The quad's bounds are offset-shifted by the paint list; the params are opaque floats it
        // cannot shift, so the ambient translation is folded in here — or a blur inside scrolled
        // content reads the wrong patch of backdrop.
        (float ox, float oy) = paint.CurrentTranslation;
        float d = MathF.Max(x: 1f, y: density);
        paint.AddShaderEffect(
            bounds: bounds,
            shaderId: ShaderId,
            p0: reach * d,
            p1: cornerRadius * d,
            p2: (bounds.X + ox) * d,
            p3: (bounds.Y + oy) * d,
            p4: bounds.Width * d,
            p5: bounds.Height * d
        );
    }

    /// <summary>
    ///     Params, in the order <see cref="PaintList.AddShaderEffect" /> takes them: the blur
    ///     radius in device pixels, the box's corner radius, then the box itself — see
    ///     <see cref="Paint" /> for why the box travels as parameters.
    /// </summary>
    private static readonly string Source =
        $$"""
        @group(0) @binding(0) var backdrop: texture_2d<f32>;
        @group(0) @binding(1) var backdrop_sampler: sampler;

        struct VertexOut {
          @builtin(position) position: vec4<f32>,
          @location(0) uv: vec2<f32>,
          @location(1) params_a: vec4<f32>,
          @location(2) params_b: vec4<f32>,
        };

        @vertex
        fn vs_main(
          @location(0) position: vec2<f32>,
          @location(1) uv: vec2<f32>,
          @location(2) params_a: vec4<f32>,
          @location(3) params_b: vec4<f32>,
        ) -> VertexOut {
          var out: VertexOut;
          out.position = vec4<f32>(position, 0.0, 1.0);
          out.uv = uv;
          out.params_a = params_a;
          out.params_b = params_b;
          return out;
        }

        @fragment
        fn fs_main(in: VertexOut) -> @location(0) vec4<f32> {
          let reach  = max(in.params_a.x, 0.5);
          let corner = in.params_a.y;
          let rect   = vec4<f32>(in.params_a.z, in.params_a.w, in.params_b.x, in.params_b.y);

          // Rounded-rect coverage, in the same analytic form the shape and glass shaders use, so a
          // blurred backdrop under a rounded panel does not square off its corners.
          let half_size = rect.zw * 0.5;
          let local = in.position.xy - (rect.xy + half_size);
          let q = abs(local) - half_size + vec2<f32>(corner);
          let sd = length(max(q, vec2<f32>(0.0))) + min(max(q.x, q.y), 0.0) - corner;
          let aa = max(fwidth(sd), 0.6);
          let coverage = 1.0 - smoothstep(-aa, aa, sd);
          if (coverage < 0.002) { discard; }

          // The spiral is rotated per pixel by interleaved gradient noise: a shared tap pattern
          // aliases into smeary streaks at wide radii, a decorrelated one reads as smooth blur
          // with fine grain. Screen-position hash only, so the pattern never shimmers.
          let ign = fract(52.9829189 * fract(dot(in.position.xy, vec2<f32>(0.06711056, 0.00583715))));
          let spin = ign * 6.2831853;
          let inv_dims = 1.0 / vec2<f32>(textureDimensions(backdrop));
          var acc = textureSampleLevel(backdrop, backdrop_sampler, in.uv, 0.0);
          var w_sum = 1.0;
          for (var i = 0; i < {{Taps}}; i++) {
            let u = (f32(i) + 0.5) / f32({{Taps}});
            let angle = f32(i) * 2.39996323 + spin;
            let offset = vec2<f32>(cos(angle), sin(angle)) * (sqrt(u) * reach) * inv_dims;
            let w = exp(-2.0 * u);
            acc += textureSampleLevel(backdrop, backdrop_sampler, in.uv + offset, 0.0) * w;
            w_sum += w;
          }

          return vec4<f32>((acc / w_sum).rgb, coverage);
        }
        """;
}
