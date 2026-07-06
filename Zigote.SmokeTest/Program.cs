using System.Diagnostics;
using System.Globalization;
using Zigote.Core;
using Zigote.Core.Math3D;
using Zigote.Core.Paint;
using Zigote.Render2D;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets.Layout;
using Zigote.Vfx;

// Boot smoke test + headless 3D golden-image capture harness.
//
// Default (2D) mode: open a window, render N frames of an opaque root, exit. Proves the renderer
// boots end-to-end (SDL3 window + wgpu device + swapchain + per-frame present) without crashing —
// the integration coverage the logic-only xUnit suite cannot provide (CLAUDE.md keeps Zigote.Tests
// headless). NOT part of `dotnet test`; run on a machine with a GPU/display.
//
// Scene (3D) mode (`ZIGOTE_SMOKE_SCENE=1`): build a deterministic 3D scene (camera + PBR sphere +
// directional light) and call Render3D each frame so the renderer exercises the full forward+ path
// (shadow → gbuffer → SSAO/SSR → bloom → AgX tonemap → TAA). Combine with ZIGOTE_SHOT to dump the
// tonemapped offscreen target to a BMP for golden-image comparison while tuning render correctness:
//
//   ZIGOTE_SMOKE_SCENE=1 ZIGOTE_SMOKE_FRAMES=16 ZIGOTE_SHOT=/tmp/shot.bmp ZIGOTE_SHOT_FRAME=10 \
//     dotnet run --project Zigote.SmokeTest
//
// Exit codes: 0 = rendered all target frames, 1 = quit early, 2 = failed to boot/render.

const uint w = 640, h = 480;

var frames = 30;
if (args.Length > 0 && int.TryParse(args[0], out var fromArg))
    frames = fromArg;
else if (int.TryParse(Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_FRAMES"), out var fromEnv))
    frames = fromEnv;

var scene = args.Contains("scene") ||
            Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_SCENE") is not null;
// Match the Blender EEVEE+AgX reference lighting (flat gray world + single moderate sun, exposure 1.0)
// for a fair A/B of material/IBL/tonemap — instead of the over-bright default studio sky.
var match = Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_MATCH") is not null;

try
{
    Console.WriteLine(
        $"[smoke] booting renderer; mode={(scene ? "3d-scene" : "2d")}; target {frames} frames…"
    );
    using var app = new App("zigote-smoke", w, h);
    app.ForceContinuousRender = true; // bounded, deterministic loop — never block on WaitEvents
    app.Root = new ColoredBox(ThemeData.Dark.Background); // opaque root (wgpu clears with alpha 0)

    if (scene)
    {
        var e = app.Engine;
        e.SceneClear();

        // Camera (kind 3 → becomes the active camera, fovy 45°). Placed on +Z looking toward the
        // origin (engine camera forward is -Z), identity rotation.
        var cam = e.SceneAddChildNode(0, "camera", 3);
        e.SceneUpdateNode(
            cam,
            0f,
            0f,
            3.5f,
            0f,
            0f,
            0f,
            1f,
            1f,
            1f,
            1f
        );

        // A mid-roughness red dielectric sphere (kind 1 mesh, primType 2) at the origin.
        var ball = e.SceneAddChildNode(0, "ball", 1);
        e.SceneSetMeshPrimitive(ball, 2);
        e.SceneSetMeshColor(
            ball,
            0.80f,
            0.18f,
            0.16f
        );
        e.SceneSetMeshRoughness(ball, 0.0f, 0.40f); // metallic=0, roughness=0.4
        e.SceneUpdateNode(
            ball,
            0f,
            0f,
            0f,
            0f,
            0f,
            0f,
            1f,
            1f,
            1f,
            1f
        );

        // Optional spot-shadow scene (ZIGOTE_SMOKE_SPOT=1): a wide ground slab + a downward spot light
        // overhead, so the sphere casts a perspective spot shadow onto the ground. Exercises the spot
        // cone falloff + per-light perspective shadow map. Replaces the directional sun for a clear read.
        var spot = scene && Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_SPOT") is not null;
        var point = scene && Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_POINT") is not null;
        var glassScene =
            scene && Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_GLASS") is not null;
        var pcss = scene && Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_PCSS") is not null;
        var ssgi = scene && Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_SSGI") is not null;
        var maskShadow = scene &&
                         Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_MASKSHADOW") is not null;
        if (ssgi)
        {
            // SSGI colour-bleed test: a saturated RED wall next to a WHITE floor and a WHITE sphere.
            // With SSGI on, the white floor/sphere near the wall should pick up a red tint (indirect bounce).
            e.SceneUpdateNode(
                cam,
                0.8f,
                1.0f,
                3.2f,
                -0.16f,
                0.06f,
                0f,
                0.985f,
                1f,
                1f,
                1f
            );
            var ground = e.SceneAddChildNode(0, "ground", 1);
            e.SceneSetMeshPrimitive(ground, 0);
            e.SceneSetMeshColor(
                ground,
                0.85f,
                0.85f,
                0.85f
            );
            e.SceneSetMeshRoughness(ground, 0f, 0.9f);
            e.SceneUpdateNode(
                ground,
                0f,
                -1f,
                0f,
                0f,
                0f,
                0f,
                1f,
                6f,
                0.2f,
                6f
            );
            var wall = e.SceneAddChildNode(0, "wall", 1);
            e.SceneSetMeshPrimitive(wall, 0);
            e.SceneSetMeshColor(
                wall,
                0.95f,
                0.04f,
                0.04f
            );
            e.SceneSetMeshRoughness(wall, 0f, 0.85f);
            e.SceneUpdateNode(
                wall,
                -1.5f,
                0.2f,
                0f,
                0f,
                0f,
                0f,
                1f,
                0.2f,
                1.4f,
                2.6f
            ); // tall red wall on the floor
            // Sphere near the wall — white by default (picks up red bounce); ZIGOTE_SMOKE_SSGI_GREEN makes
            // it green (albedo tinting → green reflects little red, so the bounce is suppressed).
            if (Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_SSGI_GREEN") is not null)
                e.SceneSetMeshColor(
                    ball,
                    0.15f,
                    0.85f,
                    0.15f
                );
            else
                e.SceneSetMeshColor(
                    ball,
                    0.9f,
                    0.9f,
                    0.9f
                );
            e.SceneSetMeshRoughness(ball, 0f, 0.7f);
            e.SceneUpdateNode(
                ball,
                -0.5f,
                -0.3f,
                0f,
                0f,
                0f,
                0f,
                1f,
                0.5f,
                0.5f,
                0.5f
            ); // sphere near the wall
            var sun3 = e.SceneAddChildNode(0, "sun", 2);
            e.SceneSetLightProperties(
                sun3,
                0,
                1f,
                0.98f,
                0.95f,
                3.0f,
                100f,
                0.4f,
                0.6f,
                true
            );
            e.SceneUpdateNode(
                sun3,
                1f,
                4f,
                2f,
                -0.30f,
                0.10f,
                0f,
                0.95f,
                1f,
                1f,
                1f
            );
        }
        else if (maskShadow)
        {
            // Alpha-masked shadow test: a raised horizontal plane with a mask material casts a cut-out
            // shadow onto the ground. ZIGOTE_SMOKE_MASKTEX=<png> gives it a checker-alpha texture (holes
            // in the shadow); without it the plane's default white texture casts a full rectangle.
            // Before the alpha-shadow pipeline, masked casters were skipped → no shadow at all.
            e.SceneUpdateNode(
                cam,
                0f,
                3.2f,
                5.5f,
                -0.28f,
                0f,
                0f,
                0.96f,
                1f,
                1f,
                1f
            );
            e.SceneSetMeshColor(
                ball,
                0.8f,
                0.2f,
                0.2f
            );
            e.SceneUpdateNode(
                ball,
                3.0f,
                0.4f,
                -1f,
                0f,
                0f,
                0f,
                1f,
                0.5f,
                0.5f,
                0.5f
            ); // off to the side
            var ground = e.SceneAddChildNode(0, "ground", 1);
            e.SceneSetMeshPrimitive(ground, 0);
            e.SceneSetMeshColor(
                ground,
                0.62f,
                0.62f,
                0.64f
            );
            e.SceneSetMeshRoughness(ground, 0f, 0.9f);
            e.SceneUpdateNode(
                ground,
                0f,
                -1f,
                0f,
                0f,
                0f,
                0f,
                1f,
                8f,
                0.2f,
                8f
            );
            // Masked plane (quad = primType 1), rotated -90° about X to lie horizontal (normal up),
            // raised above the ground so its shadow projects onto it. alpha_mode 1 = mask.
            var plane = e.SceneAddChildNode(0, "maskplane", 1);
            e.SceneSetMeshPrimitive(plane, 1);
            e.SceneSetMeshColor(
                plane,
                0.9f,
                0.85f,
                0.3f
            );
            e.SceneSetMeshAlphaMode(plane, 1); // mask
            var maskTex = Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_MASKTEX");
            if (maskTex is not null)
            {
                e.SceneSetMeshTexturePath(plane, maskTex);
                Console.WriteLine($"[smoke] mask texture: {maskTex}");
            }

            e.SceneUpdateNode(
                plane,
                0f,
                1.2f,
                0f,
                -0.7071068f,
                0f,
                0f,
                0.7071068f,
                2.2f,
                2.2f,
                2.2f
            );
            var msun = e.SceneAddChildNode(0, "sun", 2);
            e.SceneSetLightProperties(
                msun,
                0,
                1f,
                0.98f,
                0.95f,
                3.2f,
                100f,
                0.4f,
                0.6f,
                true
            );
            e.SceneUpdateNode(
                msun,
                0.4f,
                5f,
                1.2f,
                -0.34f,
                0.08f,
                0f,
                0.93f,
                1f,
                1f,
                1f
            );
        }
        else if (pcss)
        {
            // PCSS contact-hardening test: a ground slab, a sphere RESTING on it (shadow sharp at the
            // contact) and a sphere FLOATING high (shadow soft/wide). Directional sun from upper-left.
            e.SceneUpdateNode(
                cam,
                0f,
                2.6f,
                5.0f,
                -0.2164396f,
                0f,
                0f,
                0.9763146f,
                1f,
                1f,
                1f
            );
            e.SceneSetMeshColor(
                ball,
                0.80f,
                0.80f,
                0.82f
            );
            e.SceneSetMeshRoughness(ball, 0.0f, 0.6f);
            e.SceneUpdateNode(
                ball,
                -1.1f,
                -0.3f,
                0f,
                0f,
                0f,
                0f,
                1f,
                0.5f,
                0.5f,
                0.5f
            ); // resting (r=0.5 on slab top −0.8)
            var floater = e.SceneAddChildNode(0, "floater", 1);
            e.SceneSetMeshPrimitive(floater, 2);
            e.SceneSetMeshColor(
                floater,
                0.80f,
                0.80f,
                0.82f
            );
            e.SceneSetMeshRoughness(floater, 0f, 0.6f);
            e.SceneUpdateNode(
                floater,
                1.1f,
                1.6f,
                0f,
                0f,
                0f,
                0f,
                1f,
                0.5f,
                0.5f,
                0.5f
            ); // floating high
            var ground = e.SceneAddChildNode(0, "ground", 1);
            e.SceneSetMeshPrimitive(ground, 0);
            e.SceneSetMeshColor(
                ground,
                0.6f,
                0.6f,
                0.62f
            );
            e.SceneSetMeshRoughness(ground, 0f, 0.9f);
            e.SceneUpdateNode(
                ground,
                0f,
                -1f,
                0f,
                0f,
                0f,
                0f,
                1f,
                6f,
                0.2f,
                6f
            );
            var sun2 = e.SceneAddChildNode(0, "sun", 2);
            e.SceneSetLightProperties(
                sun2,
                0,
                1f,
                0.98f,
                0.95f,
                3.5f,
                100f,
                0.4f,
                0.6f,
                true
            );
            e.SceneUpdateNode(
                sun2,
                -2f,
                4f,
                1f,
                -0.30f,
                0.25f,
                0f,
                0.90f,
                1f,
                1f,
                1f
            );
        }
        else if (glassScene)
        {
            // Screen-space refraction: a clear glass sphere in front of three coloured cubes. Looking
            // through the sphere should show the cubes distorted/inverted — the refraction tell-tale.
            e.SceneSetMeshColor(
                ball,
                0.92f,
                0.96f,
                1.0f
            );
            // ZIGOTE_SMOKE_GLASS_FROST → high roughness (frosted glass); else smooth/clear.
            var frostRough =
                Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_GLASS_FROST") is not null
                    ? 0.5f
                    : 0.04f;
            e.SceneSetMeshRoughness(ball, 0.0f, frostRough);
            e.SceneSetMeshAlphaMode(ball, 3); // glass
            // ZIGOTE_SMOKE_GLASS_IOR=<f> → real-IOR glass (fresnel F0 + refraction bend), e.g. 2.4 = diamond.
            if (float.TryParse(
                    Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_GLASS_IOR"),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var glassIor
                ))
            {
                e.SceneSetMeshVolume(ball, glassIor, 1f);
                Console.WriteLine($"[smoke] glass IOR -> {glassIor}");
            }

            e.SceneUpdateNode(
                ball,
                0f,
                0f,
                1.0f,
                0f,
                0f,
                0f,
                1f,
                0.75f,
                0.75f,
                0.75f
            );

            // CLOSE content inside the glass (a "ship in the bottle") — should refract/distort.
            var inner = e.SceneAddChildNode(0, "inner", 1);
            e.SceneSetMeshPrimitive(inner, 0);
            e.SceneSetMeshColor(
                inner,
                0.95f,
                0.10f,
                0.85f
            );
            e.SceneSetMeshRoughness(inner, 0f, 0.5f);
            e.SceneUpdateNode(
                inner,
                0f,
                0f,
                1.15f,
                0f,
                0f,
                0f,
                1f,
                0.28f,
                0.28f,
                0.28f
            );

            var c0 = e.SceneAddChildNode(0, "cubeR", 1);
            e.SceneSetMeshPrimitive(c0, 0);
            e.SceneSetMeshColor(
                c0,
                0.90f,
                0.10f,
                0.10f
            );
            e.SceneSetMeshRoughness(c0, 0f, 0.5f);
            e.SceneUpdateNode(
                c0,
                -1.15f,
                0.0f,
                -1.6f,
                0f,
                0f,
                0f,
                1f,
                0.6f,
                0.6f,
                0.6f
            );
            var c1 = e.SceneAddChildNode(0, "cubeG", 1);
            e.SceneSetMeshPrimitive(c1, 0);
            e.SceneSetMeshColor(
                c1,
                0.10f,
                0.85f,
                0.20f
            );
            e.SceneSetMeshRoughness(c1, 0f, 0.5f);
            e.SceneUpdateNode(
                c1,
                0.0f,
                0.95f,
                -1.6f,
                0f,
                0f,
                0f,
                1f,
                0.6f,
                0.6f,
                0.6f
            );
            var c2 = e.SceneAddChildNode(0, "cubeB", 1);
            e.SceneSetMeshPrimitive(c2, 0);
            e.SceneSetMeshColor(
                c2,
                0.20f,
                0.30f,
                0.95f
            );
            e.SceneSetMeshRoughness(c2, 0f, 0.5f);
            e.SceneUpdateNode(
                c2,
                1.15f,
                -0.7f,
                -1.6f,
                0f,
                0f,
                0f,
                1f,
                0.6f,
                0.6f,
                0.6f
            );
        }
        else if (point)
        {
            // Point-light omnidirectional cube-shadow scene: ground slab + a point light off to one
            // side and above, so the sphere casts a cube shadow onto the ground. Exercises the depth
            // cube-array + per-direction sampling.
            e.SceneUpdateNode(
                cam,
                0f,
                2.2f,
                4.2f,
                -0.2164396f,
                0f,
                0f,
                0.9763146f,
                1f,
                1f,
                1f
            );
            e.SceneUpdateNode(
                ball,
                0f,
                1.5f,
                0f,
                0f,
                0f,
                0f,
                1f,
                1f,
                1f,
                1f
            );
            var ground = e.SceneAddChildNode(0, "ground", 1);
            e.SceneSetMeshPrimitive(ground, 0);
            e.SceneSetMeshColor(
                ground,
                0.55f,
                0.55f,
                0.58f
            );
            e.SceneSetMeshRoughness(ground, 0.0f, 0.9f);
            e.SceneUpdateNode(
                ground,
                0f,
                -1f,
                0f,
                0f,
                0f,
                0f,
                1f,
                8f,
                0.2f,
                8f
            );

            // Point light (kind 1). Position from ZIGOTE_SMOKE_POINT_POS ("x,y,z"), default up++x so the
            // sphere shadow falls toward −x; overhead (0,4,0) gives a symmetric disc (cube-seam check).
            var px = 2.0f;
            var py = 3.2f;
            var pz = 0.0f;
            var pposEnv = Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_POINT_POS");
            if (pposEnv is not null)
            {
                var parts = pposEnv.Split(',');
                if (parts.Length == 3)
                {
                    px = float.Parse(parts[0]);
                    py = float.Parse(parts[1]);
                    pz = float.Parse(parts[2]);
                }
            }

            var pl = e.SceneAddChildNode(0, "point", 2);
            e.SceneSetLightProperties(
                pl,
                1,
                1f,
                0.96f,
                0.9f,
                7.0f,
                13f,
                0.4f,
                0.6f,
                true
            );
            e.SceneUpdateNode(
                pl,
                px,
                py,
                pz,
                0f,
                0f,
                0f,
                1f,
                1f,
                1f,
                1f
            );
        }
        else if (spot)
        {
            // Raise + tilt the camera down ~25° so the ground slab (and the shadow on it) is in view.
            e.SceneUpdateNode(
                cam,
                0f,
                2.2f,
                4.2f,
                -0.2164396f,
                0f,
                0f,
                0.9763146f,
                1f,
                1f,
                1f
            );
            // Lift the ball well above the ground slab so its shadow lands as a distinct disc.
            e.SceneUpdateNode(
                ball,
                0f,
                1.4f,
                0f,
                0f,
                0f,
                0f,
                1f,
                1f,
                1f,
                1f
            );
            var ground = e.SceneAddChildNode(0, "ground", 1);
            e.SceneSetMeshPrimitive(ground, 0); // cube, scaled into a thin wide slab
            e.SceneSetMeshColor(
                ground,
                0.55f,
                0.55f,
                0.58f
            );
            e.SceneSetMeshRoughness(ground, 0.0f, 0.9f);
            e.SceneUpdateNode(
                ground,
                0f,
                -1f,
                0f,
                0f,
                0f,
                0f,
                1f,
                8f,
                0.2f,
                8f
            );

            // Spot directly overhead pointing straight down (−Z forward rotated −90° about X → −Y).
            var sp = e.SceneAddChildNode(0, "spot", 2);
            e.SceneSetLightProperties(
                sp,
                2,
                1f,
                0.97f,
                0.9f,
                18.0f,
                40f,
                0.40f,
                0.62f,
                true
            );
            e.SceneUpdateNode(
                sp,
                0f,
                5f,
                0f,
                -0.7071068f,
                0f,
                0f,
                0.7071068f,
                1f,
                1f,
                1f
            );
        }
        // Directional key light (kind 2 node, light-kind 0 = directional) from upper-right. Skipped in
        // match mode so the only directional is the Settings3D sun (mirrors Blender's single sun),
        // and in spot mode so the spot light reads cleanly.
        else if (!match)
        {
            var sun = e.SceneAddChildNode(0, "sun", 2);
            e.SceneSetLightProperties(
                sun,
                0,
                1f,
                0.98f,
                0.95f,
                3.0f,
                100f,
                0.4f,
                0.6f,
                true
            );
            e.SceneUpdateNode(
                sun,
                2f,
                4f,
                2f,
                -0.30f,
                0.10f,
                0f,
                0.95f,
                1f,
                1f,
                1f
            );
        }

        // Optional debug-view channel (ZIGOTE_SMOKE_DEBUGVIEW = DebugView enum: 1=BaseColor, 4=Roughness,
        // 5=Metallic, 2=WorldNormal, …). Read-modify-write so only DebugView changes (other settings keep
        // their engine defaults — preserves exposure/grade/etc.).
        var s = e.GetRenderSettings3D();
        Console.WriteLine(
            $"[smoke] defaults: AmbientIntensity={s.AmbientIntensity}, Exposure={s.Exposure}, SunIntensity={s.SunIntensity}"
        );
        var needSet = false;
        if (int.TryParse(
                Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_DEBUGVIEW"),
                out var dv
            ) && dv > 0)
        {
            s.DebugView = dv;
            needSet = true;
            Console.WriteLine($"[smoke] debug view = {dv}");
        }

        if (float.TryParse(Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_AMBIENT"), out var amb))
        {
            Console.WriteLine($"[smoke] AmbientIntensity {s.AmbientIntensity} -> {amb}");
            s.AmbientIntensity = amb;
            needSet = true;
        }

        if (match)
        {
            // Flat gray world (≈ Blender Background 0.25,0.27,0.30) + single moderate sun + exposure 1.0.
            s.SkyHorizonR = 0.25f;
            s.SkyHorizonG = 0.27f;
            s.SkyHorizonB = 0.30f;
            s.SkyZenithR = 0.25f;
            s.SkyZenithG = 0.27f;
            s.SkyZenithB = 0.30f;
            s.SkyGroundR = 0.25f;
            s.SkyGroundG = 0.27f;
            s.SkyGroundB = 0.30f;
            s.EnvAvgR = 0.25f;
            s.EnvAvgG = 0.27f;
            s.EnvAvgB = 0.30f;
            s.SunIntensity = 3.0f;
            s.SunAzimuthDeg = 30f;
            s.SunElevationDeg = 40f;
            s.Exposure = 1.0f;
            needSet = true;
            Console.WriteLine(
                "[smoke] match preset: flat gray sky + single moderate sun + exposure 1.0"
            );
        }

        // Atmospheric fog test (ZIGOTE_SMOKE_FOG=1): enable height fog + sun in-scatter. Pair with a
        // scene that has a receding ground (e.g. ZIGOTE_SMOKE_PCSS=1) so the aerial perspective reads.
        if (Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_FOG") is not null)
        {
            s.FogDensity = 0.14f;
            s.FogColorR = 0.55f;
            s.FogColorG = 0.62f;
            s.FogColorB = 0.72f;
            s.FogHeight = 0f;
            s.FogHeightFalloff = 0.2f;
            s.FogSunInscatter = 1.2f;
            s.FogAnisotropy = 0.78f;
            needSet = true;
            Console.WriteLine("[smoke] fog enabled (density 0.14)");
        }

        // Auto-exposure test (ZIGOTE_SMOKE_AUTOEXP=1) + a scene-brightness knob (ZIGOTE_SMOKE_SUN=<f>).
        // Capture a dim scene and a bright scene: with auto-exposure ON both should read mid-toned; OFF
        // the dim one crushes and the bright one blows out.
        if (Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_AUTOEXP") is not null)
        {
            s.AutoExposureEnabled = 1f;
            s.AutoExposureSpeed =
                1f; // snap within the fixed smoke frame count (deterministic capture)
            needSet = true;
            Console.WriteLine("[smoke] auto-exposure enabled");
        }

        if (float.TryParse(
                Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_SUN"),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var sunI
            ))
        {
            s.SunIntensity = sunI;
            needSet = true;
            Console.WriteLine($"[smoke] SunIntensity -> {sunI}");
        }

        // Physical-camera path (ZIGOTE_SMOKE_PHYSCAM=1): exercises both FFI paths the physical camera uses —
        // FOV via SceneSetCameraParams and the DoF/film/distortion grade via SetRenderSettings3D — so the
        // whole pipeline can be validated headlessly (bmpdiff vs the plain scene proves it reaches pixels).
        if (Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_PHYSCAM") is not null)
        {
            e.SceneSetCameraParams(
                cam,
                20f,
                0.1f,
                4000f
            ); // tight ~20° "telephoto" lens
            s.DofEnabled = 1f;
            s.DofFocusDistance = 3.5f;
            s.DofFStop = 1.4f;
            s.DofMaxCoc = 30f;
            s.AgxLook = 2f;
            s.WbTemperature = 0.25f;
            s.Saturation = 1.4f;
            s.VignetteStrength = 0.5f;
            s.GrainAmount = 0.06f;
            s.LensDistortionK1 = -0.25f;
            // ZIGOTE_SMOKE_BOKEH=1 also shapes the aperture (6-blade hexagonal + 1.5× anamorphic squeeze)
            // so the polygonal/anamorphic gather can be A/B-diffed against the default circular bokeh.
            if (Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_BOKEH") is not null)
            {
                s.BokehBlades = 6f;
                s.BokehAnamorphic = 1.5f;
            }

            needSet = true;
            Console.WriteLine(
                "[smoke] physcam: fov20 + shallow DoF + golden film + barrel distortion"
            );
        }

        if (needSet) e.SetRenderSettings3D(s);
    }

    // Optional native VFX particle smoke (ZIGOTE_SMOKE_VFX=1): upload a bright additive cloud each frame
    // so the GPU billboard pass actually creates its pipelines (validates the WGSL/naga) and draws —
    // visible in a ZIGOTE_SHOT dump. Cloud sits between the camera (+Z) and the ball at the origin.
    var vfx = scene && Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_VFX") is not null;
    float[]? particles = null;
    const uint pCount = 220;
    if (vfx)
    {
        particles = new float[pCount * 9];
        var rng = new Random(1234);
        for (var i = 0; i < pCount; i++)
        {
            var o = i * 9;
            particles[o] = (float)(rng.NextDouble() * 2 - 1) * 1.3f; // x
            particles[o + 1] = (float)(rng.NextDouble() * 2 - 1) * 1.3f; // y
            particles[o + 2] = (float)rng.NextDouble() * 1.2f + 0.2f; // z (in front of the ball)
            particles[o + 3] = 0.12f; // size
            particles[o + 4] = 0f; // rotation
            particles[o + 5] = 1.0f; // r
            particles[o + 6] = 0.55f; // g
            particles[o + 7] = 0.15f; // b
            particles[o + 8] = 1.0f; // a
        }

        Console.WriteLine($"[smoke] VFX: uploading {pCount} additive billboard particles/frame");
    }

    // Optional GPU compute particle smoke (ZIGOTE_SMOKE_VFX_GPU=1): build a fire-like emitter asset and
    // drive the native GPU compute path each frame (spawn budget host-side, simulation + render on GPU).
    var vfxGpu = scene && Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_VFX_GPU") is not null;
    VfxGpuEmitter? gpuEmitter = null;
    if (vfxGpu)
    {
        var asset = new VfxEmitterAsset {
            Capacity = 8192,
            SpawnRate = 600f,
            Shape = EmissionShape.Cone,
            ShapeRadius = 0.2f,
            ConeAngleDegrees = 28f,
            EmitDirection = new Vec3(0f, 1f, 0f),
            StartSpeed = new FloatRange(1.5f, 3.0f),
            StartLifetime = new FloatRange(0.8f, 1.6f),
            StartSize = new FloatRange(0.05f, 0.11f),
            Blend = VfxBlendMode.Additive,
        };
        asset.UpdateModules.Add(new GravityModule(new Vec3(0f, -1.5f, 0f)));
        asset.UpdateModules.Add(
            new ColorOverLifeModule(
                new ColorRamp(
                    [
                        new ColorStop(0f, new Color(1f, 0.9f, 0.4f)),
                        new ColorStop(0.5f, new Color(1f, 0.4f, 0.1f)),
                        new ColorStop(
                            1f,
                            new Color(
                                0.3f,
                                0.05f,
                                0f,
                                0f
                            )
                        ),
                    ]
                )
            )
        );
        asset.UpdateModules.Add(new SizeOverLifeModule(FloatCurve.Linear(1f, 0.25f)));
        gpuEmitter = new VfxGpuEmitter(asset) { Position = new Vec3(0f, -0.6f, 0f) };
        Console.WriteLine($"[smoke] VFX GPU: compute emitter, capacity {asset.Capacity}");
    }

    // Optional 2D sprite smoke (ZIGOTE_SMOKE_SPRITES=1): exercise the whole native sprite pass —
    // texture create from memory (nearest, sRGB), a CUSTOM WGSL material shader, alpha + additive
    // blends on the scene stage, and an overlay-stage sprite — so the sprite WGSL/naga validation,
    // both pipelines-per-stage, the params UBO ring and the two camera UBOs all actually run.
    // Visible in a ZIGOTE_SHOT dump (scene sprites tonemapped; overlay sprite exact-color).
    var sprites2D = scene && Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_SPRITES") is not null;
    uint spriteTex = 0, spriteShader = 0;
    float[]? spriteSceneVp = null, spriteOverlayVp = null;
    if (sprites2D)
    {
        // 64×64 magenta/yellow checkerboard, 8px cells.
        var px = new byte[64 * 64 * 4];
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            var o = (y * 64 + x) * 4;
            var check = (x / 8 + y / 8) % 2 == 0;
            px[o] = check ? (byte)255 : (byte)255; // r
            px[o + 1] = check ? (byte)0 : (byte)220; // g
            px[o + 2] = check ? (byte)255 : (byte)0; // b
            px[o + 3] = 255;
        }

        spriteTex = app.Engine.SpritesTextureCreate(
            px,
            64,
            64,
            0 /*nearest*/,
            1 /*srgb*/,
            0 /*clamp*/
        );

        // Custom material: swap R/B and multiply by params[0..3] — proves the shader contract
        // (instance layout, camera/texture/params/secondary-texture groups) end to end.
        spriteShader = app.Engine.SpritesShaderCreate(
            """
            struct Camera { view_proj: mat4x4<f32>, viewport: vec4<f32>, };
            @group(0) @binding(0) var<uniform> camera: Camera;
            @group(1) @binding(0) var tex: texture_2d<f32>;
            @group(1) @binding(1) var samp: sampler;
            struct Params { data: array<vec4<f32>, 4>, };
            @group(2) @binding(0) var<uniform> params: Params;
            @group(3) @binding(0) var tex2: texture_2d<f32>;
            @group(3) @binding(1) var samp2: sampler;
            struct VsIn {
                @location(0) pos: vec3<f32>, @location(1) rot: f32, @location(2) size: vec2<f32>,
                @location(3) uv0: vec2<f32>, @location(4) uv1: vec2<f32>, @location(5) color: vec4<f32>,
            };
            struct VsOut { @builtin(position) clip: vec4<f32>, @location(0) uv: vec2<f32>, @location(1) color: vec4<f32>, };
            @vertex fn vs_main(@builtin(vertex_index) vid: u32, in: VsIn) -> VsOut {
                var corners = array<vec2<f32>, 6>(
                    vec2<f32>(-0.5, -0.5), vec2<f32>(0.5, -0.5), vec2<f32>(0.5, 0.5),
                    vec2<f32>(-0.5, -0.5), vec2<f32>(0.5, 0.5), vec2<f32>(-0.5, 0.5));
                let corner = corners[vid];
                let local = corner * in.size;
                let c = cos(in.rot); let s = sin(in.rot);
                let world = in.pos.xy + vec2<f32>(local.x * c - local.y * s, local.x * s + local.y * c);
                var out: VsOut;
                out.clip = camera.view_proj * vec4<f32>(world, in.pos.z, 1.0);
                let t = corner + vec2<f32>(0.5, 0.5);
                out.uv = vec2<f32>(mix(in.uv0.x, in.uv1.x, t.x), mix(in.uv0.y, in.uv1.y, 1.0 - t.y));
                out.color = in.color;
                return out;
            }
            @fragment fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
                let texel = textureSample(tex, samp, in.uv);
                let col = vec4<f32>(texel.bgr, texel.a) * in.color * params.data[0];
                return vec4<f32>(col.rgb * col.a, col.a);
            }
            """
        );

        var cam2D = new Camera2D { OrthoHeight = 4f };
        spriteSceneVp = cam2D.ViewProjection(w, h).ToArray();
        spriteOverlayVp = Camera2D.PixelOverlay(w, h).ToArray();
        Console.WriteLine($"[smoke] sprites: tex={spriteTex} customShader={spriteShader}");
        if (spriteTex == 0) return 2;
        if (spriteShader == 0) Console.WriteLine("[smoke] sprites: WARNING custom shader rejected");
    }

    // Resize exercise (ZIGOTE_SMOKE_RESIZE=1): render the 3D scene at alternating sizes so the
    // renderer re-runs its resize path (depth/HDR/G-buffer/exposure targets + post bind groups) every
    // few frames — the editor viewport does this constantly; the fixed-size smoke otherwise never does.
    var resizeTest = Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_RESIZE") is not null;

    // Per-frame settings churn (ZIGOTE_SMOKE_PHYSCAM_PERFRAME=1): mimic the editor's physical-camera driver
    // by calling GetRenderSettings3D→SetRenderSettings3D every frame (varying an env-irrelevant grade knob).
    // Times the loop so the environment-rebake regression is measurable: it must NOT rebake every frame.
    var perFrameSet = scene &&
                      Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_PHYSCAM_PERFRAME") is not
                          null;
    var sw = Stopwatch.StartNew();

    var rendered = 0;
    for (; rendered < frames && !app.ShouldQuit; rendered++)
    {
        if (perFrameSet)
        {
            var ps = app.Engine.GetRenderSettings3D();
            ps.DofEnabled = 1f;
            ps.DofFocusDistance =
                3f + rendered % 10 * 0.1f; // owned, env-irrelevant: must not trigger a rebake
            // ZIGOTE_SMOKE_PHYSCAM_PERFRAME_ENV also varies an env-relevant knob (sun) each frame — this
            // SHOULD rebake every frame, demonstrating the cost the conditional-rebake fix now avoids.
            if (Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_PHYSCAM_PERFRAME_ENV") is not null)
                ps.SunIntensity = 6f + rendered % 10 * 0.05f;
            app.Engine.SetRenderSettings3D(ps);
        }

        if (scene && resizeTest)
        {
            // Cycle a few sizes so ensureDepthTexture runs repeatedly (not just once at startup).
            var sizes = new (uint, uint)[] {
                (w, h),
                (900, 620),
                (512, 400),
                (800, 600),
            };
            var (rw, rh) = sizes[rendered % sizes.Length];
            app.Engine.Render3D(rw, rh);
            app.Frame();
            continue;
        }

        if (vfx)
            app.Engine.ParticlesUpload(
                1,
                particles,
                pCount,
                0
            ); // node key 1, blend 0 = additive
        if (sprites2D)
        {
            app.Engine.SpritesBegin(
                spriteSceneVp,
                spriteOverlayVp,
                w,
                h
            );
            // Instance: pos.xyz, rot, size.xy, uv0.xy, uv1.xy, rgba (14 floats).
            // Scene stage, alpha: full checker, 2×2 units at the origin (over the ball).
            ReadOnlySpan<float> a = [0f, 0f, 0f, 0f, 2f, 2f, 0f, 0f, 1f, 1f, 1f, 1f, 1f, 1f];
            app.Engine.SpritesDraw(
                spriteTex,
                0,
                0,
                0,
                0,
                default,
                a,
                1
            );
            // Scene stage, additive: rotated top-left quarter of the sheet, tinted cyan.
            ReadOnlySpan<float> b =
                [-1.6f, 1.0f, 0f, 0.6f, 1.2f, 1.2f, 0f, 0f, 0.5f, 0.5f, 0.2f, 1f, 1f, 0.9f];
            app.Engine.SpritesDraw(
                spriteTex,
                0,
                0,
                1,
                0,
                default,
                b,
                1
            );
            // Scene stage, CUSTOM shader (params tint green-ish); falls back to default if rejected.
            ReadOnlySpan<float> c =
                [1.6f, 1.0f, 0f, 0f, 1.2f, 1.2f, 0f, 0f, 1f, 1f, 1f, 1f, 1f, 1f];
            ReadOnlySpan<float> prms = [0.3f, 1.0f, 0.3f, 1.0f];
            app.Engine.SpritesDraw(
                spriteTex,
                0,
                spriteShader,
                0,
                0,
                prms,
                c,
                1
            );
            // Overlay stage (pixel space, origin top-left): exact-color square at (40, 40)–(140, 140).
            ReadOnlySpan<float> d = [90f, 90f, 0f, 0f, 100f, 100f, 0f, 0f, 1f, 1f, 1f, 1f, 1f, 1f];
            app.Engine.SpritesDraw(
                spriteTex,
                0,
                0,
                0,
                1,
                default,
                d,
                1
            );
        }

        if (gpuEmitter != null)
        {
            const float dtSim = 1f / 60f;
            var spawn = gpuEmitter.Step(dtSim);
            app.Engine.ParticlesComputeEmit(
                2,
                gpuEmitter.BuildParams(spawn, dtSim),
                gpuEmitter.Capacity,
                gpuEmitter.Blend
            );
        }

        if (scene)
            app.Engine.Render3D(w, h); // fill the offscreen 3D target (what ZIGOTE_SHOT dumps)
        app.Frame(); // 2D frame: BeginFrame → RenderFrameV2 (capture) → EndFrame
    }

    sw.Stop();
    if (scene && rendered > 0)
        Console.WriteLine(
            $"[smoke] timing ({(perFrameSet ? "perframe-set" : "baseline")}): {rendered} frames in " +
            $"{sw.ElapsedMilliseconds} ms ({(double)sw.ElapsedMilliseconds / rendered:F2} ms/frame)"
        );

    // 2D golden-image capture (ZIGOTE_SHOT in non-scene mode): submit a deterministic paint list —
    // exercising the common CMD_RECT / CMD_BORDER commands through the ZgPaintCommand FFI struct — and
    // dump the offscreen 2D render to a BMP. This is the 2D counterpart of the 3D ZIGOTE_SHOT path and
    // the regression seam the 2D paint ABI otherwise lacks (diff with tools/bmpdiff.py).
    var uiShot = Environment.GetEnvironmentVariable("ZIGOTE_SHOT");
    if (!scene && !string.IsNullOrEmpty(uiShot))
    {
        // ZIGOTE_SMOKE_XFORM wraps the golden scene in a native transform scope
        // (CMD_TRANSFORM_PUSH/POP): "identity" must render byte-identical to no transform at all
        // (the bmpdiff gate for the transform stack); "spin" rotates+scales for a visual check.
        var xformMode = Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_XFORM");
        var paint = new PaintList();
        if (xformMode == "identity")
            paint.PushTransform(Matrix2D.Identity);
        else if (xformMode == "spin")
            paint.PushTransform(
                Matrix2D.Translation(w * 0.5f, h * 0.5f)
                * Matrix2D.Rotation(0.35f)
                * Matrix2D.Scale(0.8f, 0.8f)
                * Matrix2D.Translation(-w * 0.5f, -h * 0.5f)
            );
        paint.AddRect(
            new Rect(
                0,
                0,
                w,
                h
            ),
            ThemeData.Dark.Background
        ); // opaque background
        paint.AddRect(
            new Rect(
                40,
                40,
                160,
                100
            ),
            new Color(0.90f, 0.20f, 0.20f)
        ); // red block
        paint.AddRect(
            new Rect(
                240,
                120,
                160,
                160
            ),
            new Color(0.20f, 0.80f, 0.30f),
            16f
        ); // green rounded
        paint.AddBorder(
            new Rect(
                430,
                220,
                170,
                150
            ),
            new Color(0.30f, 0.55f, 1f),
            12f,
            6f
        ); // blue border
        if (xformMode is "identity" or "spin")
            paint.PopTransform();
        app.Engine.SubmitPaintCommands(paint);
        var okShot = app.Engine.CaptureUiBmp(uiShot, w, h);
        Console.WriteLine($"[smoke] ui-capture {(okShot ? "ok" : "FAILED")} -> {uiShot}");
        if (!okShot) return 2;
    }

    // Clip-coverage capture (ZIGOTE_SMOKE_CLIP=/path/out.bmp, non-scene mode): the same golden seam
    // but exercising CMD_CLIP_START across all three UI pipelines — shape, text (glyph atlas) and
    // image — with content deliberately overhanging the clip rect on every side.
    // ZIGOTE_SMOKE_CLIP_RADIUS=<r> rounds the clip corners; unset/0 = plain rect clip, so a radius-0
    // run must stay byte-identical across native changes to the clip path (tools/bmpdiff.py).
    var clipShot = Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_CLIP");
    if (!scene && !string.IsNullOrEmpty(clipShot))
    {
        var radius = 0f;
        if (float.TryParse(
                Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_CLIP_RADIUS"),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var r
            ))
            radius = r;

        // 64×64 checkerboard so the image pipeline's clipped corners are unambiguous in the dump.
        const int checker = 64;
        var pixels = new byte[checker * checker * 4];
        for (var py = 0; py < checker; py++)
        for (var px = 0; px < checker; px++)
        {
            var on = (px / 8 + py / 8) % 2 == 0;
            var i = (py * checker + px) * 4;
            pixels[i + 0] = on ? (byte)255 : (byte)40;
            pixels[i + 1] = on ? (byte)200 : (byte)40;
            pixels[i + 2] = on ? (byte)60 : (byte)40;
            pixels[i + 3] = 255;
        }

        var paint = new PaintList();
        paint.AddRect(
            new Rect(
                0,
                0,
                w,
                h
            ),
            ThemeData.Dark.Background
        ); // opaque background
        paint.AddClipStart(
            new Rect(
                120,
                90,
                400,
                300
            ),
            radius
        );
        paint.AddRect(
            new Rect(
                100,
                70,
                440,
                340
            ),
            new Color(0.85f, 0.35f, 0.15f)
        ); // overhangs all sides
        paint.AddRect(
            new Rect(
                120,
                90,
                120,
                80
            ),
            new Color(0.20f, 0.55f, 0.95f),
            10f
        ); // hugs the clip's top-left corner
        paint.AddText(
            "Rounded clip coverage",
            90f,
            130f,
            new Color(0.95f, 0.95f, 0.95f),
            22f
        ); // enters through the left edge
        paint.AddText(
            "corner glyphs",
            430f,
            380f,
            new Color(0.95f, 0.90f, 0.30f),
            20f
        ); // exits through the bottom-right corner
        paint.AddImage(
            new Rect(
                460,
                330,
                checker,
                checker
            ),
            checker,
            checker,
            pixels
        ); // straddles the bottom-right corner
        paint.AddClipEnd();
        paint.AddRect(
            new Rect(
                20,
                400,
                80,
                60
            ),
            new Color(0.45f, 0.85f, 0.45f),
            8f
        ); // unclipped control
        app.Engine.SubmitPaintCommands(paint);
        var okClip = app.Engine.CaptureUiBmp(clipShot, w, h);
        Console.WriteLine(
            $"[smoke] clip-capture {(okClip ? "ok" : "FAILED")} radius={radius} -> {clipShot}"
        );
        if (!okClip) return 2;
    }

    // Bidi capture (ZIGOTE_SMOKE_BIDI=/path/out.bmp, non-scene mode): mixed-direction strings
    // through the shaper's UAX#9-lite run segmentation — Latin words and multi-digit numbers
    // embedded in Arabic/Hebrew must keep their internal left-to-right order while the RTL text
    // reads right-to-left. The bundled Inter face has no Arabic/Hebrew coverage, so point
    // ZIGOTE_SMOKE_BIDI_FONT at a font that does (e.g. "/Library/Fonts/Arial Unicode.ttf" on
    // macOS); the capture is a VISUAL verification seam, not a machine-independent byte gate.
    var bidiShot = Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_BIDI");
    if (!scene && !string.IsNullOrEmpty(bidiShot))
    {
        string? bidiFamily = null;
        var bidiFont = Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_BIDI_FONT");
        if (!string.IsNullOrEmpty(bidiFont) && File.Exists(bidiFont))
        {
            if (app.Engine.LoadFont("bidi-test", bidiFont))
                bidiFamily = "bidi-test";
            else
                Console.WriteLine($"[smoke] bidi-capture: failed to load font {bidiFont}");
        }

        var paint = new PaintList();
        paint.AddRect(
            new Rect(
                0,
                0,
                w,
                h
            ),
            ThemeData.Dark.Background
        );
        var fg = new Color(0.95f, 0.95f, 0.95f);
        ReadOnlySpan<string> lines = [
            "Latin control 2026", // pure LTR — must match the pre-bidi renderer
            "مرحبا Zigote مرحبا", // Latin word inside Arabic
            "عام 2026 عام", // multi-digit number inside Arabic
            "التاريخ: 05/07/2026", // RTL label + LTR date
            "שלום Zigote!", // Hebrew + Latin + trailing punctuation
        ];
        var y = 60f;
        foreach (var line in lines)
        {
            paint.AddText(
                line,
                40f,
                y,
                fg,
                24f,
                fontFamily: bidiFamily
            );
            y += 60f;
        }

        app.Engine.SubmitPaintCommands(paint);
        var okBidi = app.Engine.CaptureUiBmp(bidiShot, w, h);
        Console.WriteLine(
            $"[smoke] bidi-capture {(okBidi ? "ok" : "FAILED")} font={bidiFont ?? "(default)"} -> {bidiShot}"
        );
        if (!okBidi) return 2;
    }

    var ok = rendered >= frames;
    Console.WriteLine(
        $"[smoke] rendered {rendered}/{frames} frames; ShouldQuit={app.ShouldQuit}; {(ok ? "OK" : "INCOMPLETE")}"
    );
    return ok ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[smoke] FAILED to boot/render: {ex}");
    return 2;
}