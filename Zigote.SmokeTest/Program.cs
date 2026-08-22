using System.Diagnostics;
using System.Globalization;
using Zigote.Core;
using Zigote.Core.Engine;
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

int frames = 30;
if (args.Length > 0 && int.TryParse(s: args[0], result: out int fromArg))
    frames = fromArg;
else if (int.TryParse(
             s: Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_FRAMES"),
             result: out int fromEnv
         ))
    frames = fromEnv;

bool scene = args.Contains("scene") ||
             Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_SCENE") is not null;
// Match the Blender EEVEE+AgX reference lighting (flat gray world + single moderate sun, exposure 1.0)
// for a fair A/B of material/IBL/tonemap — instead of the over-bright default studio sky.
bool match = Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_MATCH") is not null;

try
{
    Console.WriteLine(
        $"[smoke] booting renderer; mode={(scene ? "3d-scene" : "2d")}; target {frames} frames…"
    );
    using var app = new App(title: "zigote-smoke", width: w, height: h);
    app.ForceContinuousRender = true; // bounded, deterministic loop — never block on WaitEvents
    app.Root = new ColoredBox(ThemeData.Dark.Background); // opaque root (wgpu clears with alpha 0)

    if (scene)
    {
        var e = app.Engine;
        e.SceneClear();

        // Camera (kind 3 → becomes the active camera, fovy 45°). Placed on +Z looking toward the
        // origin (engine camera forward is -Z), identity rotation.
        ulong cam = e.SceneAddChildNode(parentHandle: 0, name: "camera", kind: 3);
        e.SceneUpdateNode(
            nodeHandle: cam,
            x: 0f,
            y: 0f,
            z: 3.5f,
            qx: 0f,
            qy: 0f,
            qz: 0f,
            qw: 1f,
            sx: 1f,
            sy: 1f,
            sz: 1f
        );

        // A mid-roughness red dielectric sphere (kind 1 mesh, primType 2) at the origin.
        ulong ball = e.SceneAddChildNode(parentHandle: 0, name: "ball", kind: 1);
        e.SceneSetMeshPrimitive(nodeHandle: ball, primType: 2);
        e.SceneSetMeshColor(
            nodeHandle: ball,
            r: 0.80f,
            g: 0.18f,
            b: 0.16f
        );
        e.SceneSetMeshRoughness(
            nodeHandle: ball,
            metallic: 0.0f,
            roughness: 0.40f
        ); // metallic=0, roughness=0.4
        e.SceneUpdateNode(
            nodeHandle: ball,
            x: 0f,
            y: 0f,
            z: 0f,
            qx: 0f,
            qy: 0f,
            qz: 0f,
            qw: 1f,
            sx: 1f,
            sy: 1f,
            sz: 1f
        );

        // Optional spot-shadow scene (ZIGOTE_SMOKE_SPOT=1): a wide ground slab + a downward spot light
        // overhead, so the sphere casts a perspective spot shadow onto the ground. Exercises the spot
        // cone falloff + per-light perspective shadow map. Replaces the directional sun for a clear read.
        bool spot = scene && Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_SPOT") is not null;
        bool point = scene && Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_POINT") is not null;
        bool glassScene =
            scene && Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_GLASS") is not null;
        bool pcss = scene && Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_PCSS") is not null;
        bool ssgi = scene && Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_SSGI") is not null;
        bool maskShadow = scene &&
                          Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_MASKSHADOW") is not null;
        if (ssgi)
        {
            // SSGI colour-bleed test: a saturated RED wall next to a WHITE floor and a WHITE sphere.
            // With SSGI on, the white floor/sphere near the wall should pick up a red tint (indirect bounce).
            e.SceneUpdateNode(
                nodeHandle: cam,
                x: 0.8f,
                y: 1.0f,
                z: 3.2f,
                qx: -0.16f,
                qy: 0.06f,
                qz: 0f,
                qw: 0.985f,
                sx: 1f,
                sy: 1f,
                sz: 1f
            );
            ulong ground = e.SceneAddChildNode(parentHandle: 0, name: "ground", kind: 1);
            e.SceneSetMeshPrimitive(nodeHandle: ground, primType: 0);
            e.SceneSetMeshColor(
                nodeHandle: ground,
                r: 0.85f,
                g: 0.85f,
                b: 0.85f
            );
            e.SceneSetMeshRoughness(nodeHandle: ground, metallic: 0f, roughness: 0.9f);
            e.SceneUpdateNode(
                nodeHandle: ground,
                x: 0f,
                y: -1f,
                z: 0f,
                qx: 0f,
                qy: 0f,
                qz: 0f,
                qw: 1f,
                sx: 6f,
                sy: 0.2f,
                sz: 6f
            );
            ulong wall = e.SceneAddChildNode(parentHandle: 0, name: "wall", kind: 1);
            e.SceneSetMeshPrimitive(nodeHandle: wall, primType: 0);
            e.SceneSetMeshColor(
                nodeHandle: wall,
                r: 0.95f,
                g: 0.04f,
                b: 0.04f
            );
            e.SceneSetMeshRoughness(nodeHandle: wall, metallic: 0f, roughness: 0.85f);
            e.SceneUpdateNode(
                nodeHandle: wall,
                x: -1.5f,
                y: 0.2f,
                z: 0f,
                qx: 0f,
                qy: 0f,
                qz: 0f,
                qw: 1f,
                sx: 0.2f,
                sy: 1.4f,
                sz: 2.6f
            ); // tall red wall on the floor
            // Sphere near the wall — white by default (picks up red bounce); ZIGOTE_SMOKE_SSGI_GREEN makes
            // it green (albedo tinting → green reflects little red, so the bounce is suppressed).
            if (Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_SSGI_GREEN") is not null)
            {
                e.SceneSetMeshColor(
                    nodeHandle: ball,
                    r: 0.15f,
                    g: 0.85f,
                    b: 0.15f
                );
            }
            else
            {
                e.SceneSetMeshColor(
                    nodeHandle: ball,
                    r: 0.9f,
                    g: 0.9f,
                    b: 0.9f
                );
            }

            e.SceneSetMeshRoughness(nodeHandle: ball, metallic: 0f, roughness: 0.7f);
            e.SceneUpdateNode(
                nodeHandle: ball,
                x: -0.5f,
                y: -0.3f,
                z: 0f,
                qx: 0f,
                qy: 0f,
                qz: 0f,
                qw: 1f,
                sx: 0.5f,
                sy: 0.5f,
                sz: 0.5f
            ); // sphere near the wall
            ulong sun3 = e.SceneAddChildNode(parentHandle: 0, name: "sun", kind: 2);
            e.SceneSetLightProperties(
                nodeHandle: sun3,
                kind: 0,
                r: 1f,
                g: 0.98f,
                b: 0.95f,
                intensity: 3.0f,
                range: 100f,
                innerAngle: 0.4f,
                outerAngle: 0.6f,
                castShadows: true
            );
            e.SceneUpdateNode(
                nodeHandle: sun3,
                x: 1f,
                y: 4f,
                z: 2f,
                qx: -0.30f,
                qy: 0.10f,
                qz: 0f,
                qw: 0.95f,
                sx: 1f,
                sy: 1f,
                sz: 1f
            );
        }
        else if (maskShadow)
        {
            // Alpha-masked shadow test: a raised horizontal plane with a mask material casts a cut-out
            // shadow onto the ground. ZIGOTE_SMOKE_MASKTEX=<png> gives it a checker-alpha texture (holes
            // in the shadow); without it the plane's default white texture casts a full rectangle.
            // Before the alpha-shadow pipeline, masked casters were skipped → no shadow at all.
            e.SceneUpdateNode(
                nodeHandle: cam,
                x: 0f,
                y: 3.2f,
                z: 5.5f,
                qx: -0.28f,
                qy: 0f,
                qz: 0f,
                qw: 0.96f,
                sx: 1f,
                sy: 1f,
                sz: 1f
            );
            e.SceneSetMeshColor(
                nodeHandle: ball,
                r: 0.8f,
                g: 0.2f,
                b: 0.2f
            );
            e.SceneUpdateNode(
                nodeHandle: ball,
                x: 3.0f,
                y: 0.4f,
                z: -1f,
                qx: 0f,
                qy: 0f,
                qz: 0f,
                qw: 1f,
                sx: 0.5f,
                sy: 0.5f,
                sz: 0.5f
            ); // off to the side
            ulong ground = e.SceneAddChildNode(parentHandle: 0, name: "ground", kind: 1);
            e.SceneSetMeshPrimitive(nodeHandle: ground, primType: 0);
            e.SceneSetMeshColor(
                nodeHandle: ground,
                r: 0.62f,
                g: 0.62f,
                b: 0.64f
            );
            e.SceneSetMeshRoughness(nodeHandle: ground, metallic: 0f, roughness: 0.9f);
            e.SceneUpdateNode(
                nodeHandle: ground,
                x: 0f,
                y: -1f,
                z: 0f,
                qx: 0f,
                qy: 0f,
                qz: 0f,
                qw: 1f,
                sx: 8f,
                sy: 0.2f,
                sz: 8f
            );
            // Masked plane (quad = primType 1), rotated -90° about X to lie horizontal (normal up),
            // raised above the ground so its shadow projects onto it. alpha_mode 1 = mask.
            ulong plane = e.SceneAddChildNode(parentHandle: 0, name: "maskplane", kind: 1);
            e.SceneSetMeshPrimitive(nodeHandle: plane, primType: 1);
            e.SceneSetMeshColor(
                nodeHandle: plane,
                r: 0.9f,
                g: 0.85f,
                b: 0.3f
            );
            e.SceneSetMeshAlphaMode(nodeHandle: plane, mode: 1); // mask
            string? maskTex = Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_MASKTEX");
            if (maskTex is not null)
            {
                e.SceneSetMeshTexturePath(nodeHandle: plane, path: maskTex);
                Console.WriteLine($"[smoke] mask texture: {maskTex}");
            }

            e.SceneUpdateNode(
                nodeHandle: plane,
                x: 0f,
                y: 1.2f,
                z: 0f,
                qx: -0.7071068f,
                qy: 0f,
                qz: 0f,
                qw: 0.7071068f,
                sx: 2.2f,
                sy: 2.2f,
                sz: 2.2f
            );
            ulong msun = e.SceneAddChildNode(parentHandle: 0, name: "sun", kind: 2);
            e.SceneSetLightProperties(
                nodeHandle: msun,
                kind: 0,
                r: 1f,
                g: 0.98f,
                b: 0.95f,
                intensity: 3.2f,
                range: 100f,
                innerAngle: 0.4f,
                outerAngle: 0.6f,
                castShadows: true
            );
            e.SceneUpdateNode(
                nodeHandle: msun,
                x: 0.4f,
                y: 5f,
                z: 1.2f,
                qx: -0.34f,
                qy: 0.08f,
                qz: 0f,
                qw: 0.93f,
                sx: 1f,
                sy: 1f,
                sz: 1f
            );
        }
        else if (pcss)
        {
            // PCSS contact-hardening test: a ground slab, a sphere RESTING on it (shadow sharp at the
            // contact) and a sphere FLOATING high (shadow soft/wide). Directional sun from upper-left.
            e.SceneUpdateNode(
                nodeHandle: cam,
                x: 0f,
                y: 2.6f,
                z: 5.0f,
                qx: -0.2164396f,
                qy: 0f,
                qz: 0f,
                qw: 0.9763146f,
                sx: 1f,
                sy: 1f,
                sz: 1f
            );
            e.SceneSetMeshColor(
                nodeHandle: ball,
                r: 0.80f,
                g: 0.80f,
                b: 0.82f
            );
            e.SceneSetMeshRoughness(nodeHandle: ball, metallic: 0.0f, roughness: 0.6f);
            e.SceneUpdateNode(
                nodeHandle: ball,
                x: -1.1f,
                y: -0.3f,
                z: 0f,
                qx: 0f,
                qy: 0f,
                qz: 0f,
                qw: 1f,
                sx: 0.5f,
                sy: 0.5f,
                sz: 0.5f
            ); // resting (r=0.5 on slab top −0.8)
            ulong floater = e.SceneAddChildNode(parentHandle: 0, name: "floater", kind: 1);
            e.SceneSetMeshPrimitive(nodeHandle: floater, primType: 2);
            e.SceneSetMeshColor(
                nodeHandle: floater,
                r: 0.80f,
                g: 0.80f,
                b: 0.82f
            );
            e.SceneSetMeshRoughness(nodeHandle: floater, metallic: 0f, roughness: 0.6f);
            e.SceneUpdateNode(
                nodeHandle: floater,
                x: 1.1f,
                y: 1.6f,
                z: 0f,
                qx: 0f,
                qy: 0f,
                qz: 0f,
                qw: 1f,
                sx: 0.5f,
                sy: 0.5f,
                sz: 0.5f
            ); // floating high
            ulong ground = e.SceneAddChildNode(parentHandle: 0, name: "ground", kind: 1);
            e.SceneSetMeshPrimitive(nodeHandle: ground, primType: 0);
            e.SceneSetMeshColor(
                nodeHandle: ground,
                r: 0.6f,
                g: 0.6f,
                b: 0.62f
            );
            e.SceneSetMeshRoughness(nodeHandle: ground, metallic: 0f, roughness: 0.9f);
            e.SceneUpdateNode(
                nodeHandle: ground,
                x: 0f,
                y: -1f,
                z: 0f,
                qx: 0f,
                qy: 0f,
                qz: 0f,
                qw: 1f,
                sx: 6f,
                sy: 0.2f,
                sz: 6f
            );
            ulong sun2 = e.SceneAddChildNode(parentHandle: 0, name: "sun", kind: 2);
            e.SceneSetLightProperties(
                nodeHandle: sun2,
                kind: 0,
                r: 1f,
                g: 0.98f,
                b: 0.95f,
                intensity: 3.5f,
                range: 100f,
                innerAngle: 0.4f,
                outerAngle: 0.6f,
                castShadows: true
            );
            e.SceneUpdateNode(
                nodeHandle: sun2,
                x: -2f,
                y: 4f,
                z: 1f,
                qx: -0.30f,
                qy: 0.25f,
                qz: 0f,
                qw: 0.90f,
                sx: 1f,
                sy: 1f,
                sz: 1f
            );
        }
        else if (glassScene)
        {
            // Screen-space refraction: a clear glass sphere in front of three coloured cubes. Looking
            // through the sphere should show the cubes distorted/inverted — the refraction tell-tale.
            e.SceneSetMeshColor(
                nodeHandle: ball,
                r: 0.92f,
                g: 0.96f,
                b: 1.0f
            );
            // ZIGOTE_SMOKE_GLASS_FROST → high roughness (frosted glass); else smooth/clear.
            float frostRough =
                Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_GLASS_FROST") is not null
                    ? 0.5f
                    : 0.04f;
            e.SceneSetMeshRoughness(nodeHandle: ball, metallic: 0.0f, roughness: frostRough);
            e.SceneSetMeshAlphaMode(nodeHandle: ball, mode: 3); // glass
            // ZIGOTE_SMOKE_GLASS_IOR=<f> → real-IOR glass (fresnel F0 + refraction bend), e.g. 2.4 = diamond.
            if (float.TryParse(
                    s: Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_GLASS_IOR"),
                    style: NumberStyles.Float,
                    provider: CultureInfo.InvariantCulture,
                    result: out float glassIor
                ))
            {
                e.SceneSetMeshVolume(nodeHandle: ball, ior: glassIor, transmission: 1f);
                Console.WriteLine($"[smoke] glass IOR -> {glassIor}");
            }

            e.SceneUpdateNode(
                nodeHandle: ball,
                x: 0f,
                y: 0f,
                z: 1.0f,
                qx: 0f,
                qy: 0f,
                qz: 0f,
                qw: 1f,
                sx: 0.75f,
                sy: 0.75f,
                sz: 0.75f
            );

            // CLOSE content inside the glass (a "ship in the bottle") — should refract/distort.
            ulong inner = e.SceneAddChildNode(parentHandle: 0, name: "inner", kind: 1);
            e.SceneSetMeshPrimitive(nodeHandle: inner, primType: 0);
            e.SceneSetMeshColor(
                nodeHandle: inner,
                r: 0.95f,
                g: 0.10f,
                b: 0.85f
            );
            e.SceneSetMeshRoughness(nodeHandle: inner, metallic: 0f, roughness: 0.5f);
            e.SceneUpdateNode(
                nodeHandle: inner,
                x: 0f,
                y: 0f,
                z: 1.15f,
                qx: 0f,
                qy: 0f,
                qz: 0f,
                qw: 1f,
                sx: 0.28f,
                sy: 0.28f,
                sz: 0.28f
            );

            ulong c0 = e.SceneAddChildNode(parentHandle: 0, name: "cubeR", kind: 1);
            e.SceneSetMeshPrimitive(nodeHandle: c0, primType: 0);
            e.SceneSetMeshColor(
                nodeHandle: c0,
                r: 0.90f,
                g: 0.10f,
                b: 0.10f
            );
            e.SceneSetMeshRoughness(nodeHandle: c0, metallic: 0f, roughness: 0.5f);
            e.SceneUpdateNode(
                nodeHandle: c0,
                x: -1.15f,
                y: 0.0f,
                z: -1.6f,
                qx: 0f,
                qy: 0f,
                qz: 0f,
                qw: 1f,
                sx: 0.6f,
                sy: 0.6f,
                sz: 0.6f
            );
            ulong c1 = e.SceneAddChildNode(parentHandle: 0, name: "cubeG", kind: 1);
            e.SceneSetMeshPrimitive(nodeHandle: c1, primType: 0);
            e.SceneSetMeshColor(
                nodeHandle: c1,
                r: 0.10f,
                g: 0.85f,
                b: 0.20f
            );
            e.SceneSetMeshRoughness(nodeHandle: c1, metallic: 0f, roughness: 0.5f);
            e.SceneUpdateNode(
                nodeHandle: c1,
                x: 0.0f,
                y: 0.95f,
                z: -1.6f,
                qx: 0f,
                qy: 0f,
                qz: 0f,
                qw: 1f,
                sx: 0.6f,
                sy: 0.6f,
                sz: 0.6f
            );
            ulong c2 = e.SceneAddChildNode(parentHandle: 0, name: "cubeB", kind: 1);
            e.SceneSetMeshPrimitive(nodeHandle: c2, primType: 0);
            e.SceneSetMeshColor(
                nodeHandle: c2,
                r: 0.20f,
                g: 0.30f,
                b: 0.95f
            );
            e.SceneSetMeshRoughness(nodeHandle: c2, metallic: 0f, roughness: 0.5f);
            e.SceneUpdateNode(
                nodeHandle: c2,
                x: 1.15f,
                y: -0.7f,
                z: -1.6f,
                qx: 0f,
                qy: 0f,
                qz: 0f,
                qw: 1f,
                sx: 0.6f,
                sy: 0.6f,
                sz: 0.6f
            );
        }
        else if (point)
        {
            // Point-light omnidirectional cube-shadow scene: ground slab + a point light off to one
            // side and above, so the sphere casts a cube shadow onto the ground. Exercises the depth
            // cube-array + per-direction sampling.
            e.SceneUpdateNode(
                nodeHandle: cam,
                x: 0f,
                y: 2.2f,
                z: 4.2f,
                qx: -0.2164396f,
                qy: 0f,
                qz: 0f,
                qw: 0.9763146f,
                sx: 1f,
                sy: 1f,
                sz: 1f
            );
            e.SceneUpdateNode(
                nodeHandle: ball,
                x: 0f,
                y: 1.5f,
                z: 0f,
                qx: 0f,
                qy: 0f,
                qz: 0f,
                qw: 1f,
                sx: 1f,
                sy: 1f,
                sz: 1f
            );
            ulong ground = e.SceneAddChildNode(parentHandle: 0, name: "ground", kind: 1);
            e.SceneSetMeshPrimitive(nodeHandle: ground, primType: 0);
            e.SceneSetMeshColor(
                nodeHandle: ground,
                r: 0.55f,
                g: 0.55f,
                b: 0.58f
            );
            e.SceneSetMeshRoughness(nodeHandle: ground, metallic: 0.0f, roughness: 0.9f);
            e.SceneUpdateNode(
                nodeHandle: ground,
                x: 0f,
                y: -1f,
                z: 0f,
                qx: 0f,
                qy: 0f,
                qz: 0f,
                qw: 1f,
                sx: 8f,
                sy: 0.2f,
                sz: 8f
            );

            // Point light (kind 1). Position from ZIGOTE_SMOKE_POINT_POS ("x,y,z"), default up++x so the
            // sphere shadow falls toward −x; overhead (0,4,0) gives a symmetric disc (cube-seam check).
            float px = 2.0f;
            float py = 3.2f;
            float pz = 0.0f;
            string? pposEnv = Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_POINT_POS");
            if (pposEnv is not null)
            {
                string[] parts = pposEnv.Split(',');
                if (parts.Length == 3)
                {
                    px = float.Parse(parts[0]);
                    py = float.Parse(parts[1]);
                    pz = float.Parse(parts[2]);
                }
            }

            ulong pl = e.SceneAddChildNode(parentHandle: 0, name: "point", kind: 2);
            e.SceneSetLightProperties(
                nodeHandle: pl,
                kind: 1,
                r: 1f,
                g: 0.96f,
                b: 0.9f,
                intensity: 7.0f,
                range: 13f,
                innerAngle: 0.4f,
                outerAngle: 0.6f,
                castShadows: true
            );
            e.SceneUpdateNode(
                nodeHandle: pl,
                x: px,
                y: py,
                z: pz,
                qx: 0f,
                qy: 0f,
                qz: 0f,
                qw: 1f,
                sx: 1f,
                sy: 1f,
                sz: 1f
            );
        }
        else if (spot)
        {
            // Raise + tilt the camera down ~25° so the ground slab (and the shadow on it) is in view.
            e.SceneUpdateNode(
                nodeHandle: cam,
                x: 0f,
                y: 2.2f,
                z: 4.2f,
                qx: -0.2164396f,
                qy: 0f,
                qz: 0f,
                qw: 0.9763146f,
                sx: 1f,
                sy: 1f,
                sz: 1f
            );
            // Lift the ball well above the ground slab so its shadow lands as a distinct disc.
            e.SceneUpdateNode(
                nodeHandle: ball,
                x: 0f,
                y: 1.4f,
                z: 0f,
                qx: 0f,
                qy: 0f,
                qz: 0f,
                qw: 1f,
                sx: 1f,
                sy: 1f,
                sz: 1f
            );
            ulong ground = e.SceneAddChildNode(parentHandle: 0, name: "ground", kind: 1);
            e.SceneSetMeshPrimitive(
                nodeHandle: ground,
                primType: 0
            ); // cube, scaled into a thin wide slab
            e.SceneSetMeshColor(
                nodeHandle: ground,
                r: 0.55f,
                g: 0.55f,
                b: 0.58f
            );
            e.SceneSetMeshRoughness(nodeHandle: ground, metallic: 0.0f, roughness: 0.9f);
            e.SceneUpdateNode(
                nodeHandle: ground,
                x: 0f,
                y: -1f,
                z: 0f,
                qx: 0f,
                qy: 0f,
                qz: 0f,
                qw: 1f,
                sx: 8f,
                sy: 0.2f,
                sz: 8f
            );

            // Spot directly overhead pointing straight down (−Z forward rotated −90° about X → −Y).
            ulong sp = e.SceneAddChildNode(parentHandle: 0, name: "spot", kind: 2);
            e.SceneSetLightProperties(
                nodeHandle: sp,
                kind: 2,
                r: 1f,
                g: 0.97f,
                b: 0.9f,
                intensity: 18.0f,
                range: 40f,
                innerAngle: 0.40f,
                outerAngle: 0.62f,
                castShadows: true
            );
            e.SceneUpdateNode(
                nodeHandle: sp,
                x: 0f,
                y: 5f,
                z: 0f,
                qx: -0.7071068f,
                qy: 0f,
                qz: 0f,
                qw: 0.7071068f,
                sx: 1f,
                sy: 1f,
                sz: 1f
            );
        }
        // Directional key light (kind 2 node, light-kind 0 = directional) from upper-right. Skipped in
        // match mode so the only directional is the Settings3D sun (mirrors Blender's single sun),
        // and in spot mode so the spot light reads cleanly.
        else if (!match)
        {
            ulong sun = e.SceneAddChildNode(parentHandle: 0, name: "sun", kind: 2);
            e.SceneSetLightProperties(
                nodeHandle: sun,
                kind: 0,
                r: 1f,
                g: 0.98f,
                b: 0.95f,
                intensity: 3.0f,
                range: 100f,
                innerAngle: 0.4f,
                outerAngle: 0.6f,
                castShadows: true
            );
            e.SceneUpdateNode(
                nodeHandle: sun,
                x: 2f,
                y: 4f,
                z: 2f,
                qx: -0.30f,
                qy: 0.10f,
                qz: 0f,
                qw: 0.95f,
                sx: 1f,
                sy: 1f,
                sz: 1f
            );
        }

        // Optional debug-view channel (ZIGOTE_SMOKE_DEBUGVIEW = DebugView enum: 1=BaseColor, 4=Roughness,
        // 5=Metallic, 2=WorldNormal, …). Read-modify-write so only DebugView changes (other settings keep
        // their engine defaults — preserves exposure/grade/etc.).
        var s = e.GetRenderSettings3D();
        Console.WriteLine(
            $"[smoke] defaults: AmbientIntensity={s.AmbientIntensity}, Exposure={s.Exposure}, SunIntensity={s.SunIntensity}"
        );
        bool needSet = false;
        if (int.TryParse(
                s: Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_DEBUGVIEW"),
                result: out int dv
            ) && dv > 0)
        {
            s.DebugView = dv;
            needSet = true;
            Console.WriteLine($"[smoke] debug view = {dv}");
        }

        if (float.TryParse(
                s: Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_AMBIENT"),
                result: out float amb
            ))
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
                s: Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_SUN"),
                style: NumberStyles.Float,
                provider: CultureInfo.InvariantCulture,
                result: out float sunI
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
                nodeHandle: cam,
                fovyDegrees: 20f,
                near: 0.1f,
                far: 4000f
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
    bool vfx = scene && Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_VFX") is not null;
    float[]? particles = null;
    const uint pCount = 220;
    if (vfx)
    {
        particles = new float[pCount * 9];
        var rng = new Random(1234);
        for (int i = 0; i < pCount; i++)
        {
            int o = i * 9;
            particles[o] = (float)((rng.NextDouble() * 2) - 1) * 1.3f; // x
            particles[o + 1] = (float)((rng.NextDouble() * 2) - 1) * 1.3f; // y
            particles[o + 2] = ((float)rng.NextDouble() * 1.2f) + 0.2f; // z (in front of the ball)
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
    bool vfxGpu = scene && Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_VFX_GPU") is not null;
    VfxGpuEmitter? gpuEmitter = null;
    if (vfxGpu)
    {
        var asset = new VfxEmitterAsset {
            Capacity = 8192,
            SpawnRate = 600f,
            Shape = EmissionShape.Cone,
            ShapeRadius = 0.2f,
            ConeAngleDegrees = 28f,
            EmitDirection = new Vec3(x: 0f, y: 1f, z: 0f),
            StartSpeed = new FloatRange(min: 1.5f, max: 3.0f),
            StartLifetime = new FloatRange(min: 0.8f, max: 1.6f),
            StartSize = new FloatRange(min: 0.05f, max: 0.11f),
            Blend = VfxBlendMode.Additive,
        };
        asset.UpdateModules.Add(new GravityModule(new Vec3(x: 0f, y: -1.5f, z: 0f)));
        asset.UpdateModules.Add(
            new ColorOverLifeModule(
                new ColorRamp(
                    [
                        new ColorStop(position: 0f, color: new Color(r: 1f, g: 0.9f, b: 0.4f)),
                        new ColorStop(position: 0.5f, color: new Color(r: 1f, g: 0.4f, b: 0.1f)),
                        new ColorStop(
                            position: 1f,
                            color: new Color(
                                r: 0.3f,
                                g: 0.05f,
                                b: 0f,
                                a: 0f
                            )
                        ),
                    ]
                )
            )
        );
        asset.UpdateModules.Add(new SizeOverLifeModule(FloatCurve.Linear(from: 1f, to: 0.25f)));
        gpuEmitter = new VfxGpuEmitter(asset) { Position = new Vec3(x: 0f, y: -0.6f, z: 0f) };
        Console.WriteLine($"[smoke] VFX GPU: compute emitter, capacity {asset.Capacity}");
    }

    // Optional 2D sprite smoke (ZIGOTE_SMOKE_SPRITES=1): exercise the whole native sprite pass —
    // texture create from memory (nearest, sRGB), a CUSTOM WGSL material shader, alpha + additive
    // blends on the scene stage, and an overlay-stage sprite — so the sprite WGSL/naga validation,
    // both pipelines-per-stage, the params UBO ring and the two camera UBOs all actually run.
    // Visible in a ZIGOTE_SHOT dump (scene sprites tonemapped; overlay sprite exact-color).
    bool sprites2D =
        scene && Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_SPRITES") is not null;
    uint spriteTex = 0, spriteShader = 0;
    float[]? spriteSceneVp = null, spriteOverlayVp = null;
    if (sprites2D)
    {
        // 64×64 magenta/yellow checkerboard, 8px cells.
        byte[] px = new byte[64 * 64 * 4];
        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
        {
            int o = ((y * 64) + x) * 4;
            bool check = ((x / 8) + (y / 8)) % 2 == 0;
            px[o] = check ? (byte)255 : (byte)255; // r
            px[o + 1] = check ? (byte)0 : (byte)220; // g
            px[o + 2] = check ? (byte)255 : (byte)0; // b
            px[o + 3] = 255;
        }

        spriteTex = app.Engine.SpritesTextureCreate(
            rgba: px,
            width: 64,
            height: 64,
            filter: 0 /*nearest*/,
            srgb: 1 /*srgb*/,
            wrap: 0 /*clamp*/
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
                @location(6) shape: vec2<f32>,
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
        spriteSceneVp = cam2D.ViewProjection(viewportW: w, viewportH: h).ToArray();
        spriteOverlayVp = Camera2D.PixelOverlay(viewportW: w, viewportH: h).ToArray();
        Console.WriteLine($"[smoke] sprites: tex={spriteTex} customShader={spriteShader}");
        if (spriteTex == 0) return 2;
        if (spriteShader == 0) Console.WriteLine("[smoke] sprites: WARNING custom shader rejected");
    }

    // Resize exercise (ZIGOTE_SMOKE_RESIZE=1): render the 3D scene at alternating sizes so the
    // renderer re-runs its resize path (depth/HDR/G-buffer/exposure targets + post bind groups) every
    // few frames — the editor viewport does this constantly; the fixed-size smoke otherwise never does.
    bool resizeTest = Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_RESIZE") is not null;

    // Per-frame settings churn (ZIGOTE_SMOKE_PHYSCAM_PERFRAME=1): mimic the editor's physical-camera driver
    // by calling GetRenderSettings3D→SetRenderSettings3D every frame (varying an env-irrelevant grade knob).
    // Times the loop so the environment-rebake regression is measurable: it must NOT rebake every frame.
    bool perFrameSet = scene &&
                       Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_PHYSCAM_PERFRAME") is not
                           null;
    var sw = Stopwatch.StartNew();

    int rendered = 0;
    for (; rendered < frames && !app.ShouldQuit; rendered++)
    {
        if (perFrameSet)
        {
            var ps = app.Engine.GetRenderSettings3D();
            ps.DofEnabled = 1f;
            ps.DofFocusDistance =
                3f + (rendered % 10 * 0.1f); // owned, env-irrelevant: must not trigger a rebake
            // ZIGOTE_SMOKE_PHYSCAM_PERFRAME_ENV also varies an env-relevant knob (sun) each frame — this
            // SHOULD rebake every frame, demonstrating the cost the conditional-rebake fix now avoids.
            if (Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_PHYSCAM_PERFRAME_ENV") is not null)
                ps.SunIntensity = 6f + (rendered % 10 * 0.05f);
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
            (uint rw, uint rh) = sizes[rendered % sizes.Length];
            app.Engine.Render3D(width: rw, height: rh);
            app.Frame();
            continue;
        }

        if (vfx)
        {
            app.Engine.ParticlesUpload(
                nodeHandle: 1,
                data: particles,
                count: pCount,
                blend: 0
            ); // node key 1, blend 0 = additive
        }

        if (sprites2D)
        {
            app.Engine.SpritesBegin(
                sceneViewProj: spriteSceneVp,
                overlayViewProj: spriteOverlayVp,
                viewportW: w,
                viewportH: h
            );
            // Instance: pos.xyz, rot, size.xy, uv0.xy, uv1.xy, rgba, corner_radius,
            // border_width (16 floats).
            // Scene stage, alpha: full checker, 2×2 units at the origin (over the ball).
            ReadOnlySpan<float> a =
                [0f, 0f, 0f, 0f, 2f, 2f, 0f, 0f, 1f, 1f, 1f, 1f, 1f, 1f, 0f, 0f];
            app.Engine.SpritesDraw(
                texture: spriteTex,
                texture2: 0,
                shader: 0,
                blend: 0,
                stage: 0,
                materialParams: default,
                instances: a,
                count: 1
            );
            // Scene stage, additive: rotated top-left quarter of the sheet, tinted cyan.
            ReadOnlySpan<float> b =
                [-1.6f, 1.0f, 0f, 0.6f, 1.2f, 1.2f, 0f, 0f, 0.5f, 0.5f, 0.2f, 1f, 1f, 0.9f, 0f, 0f];
            app.Engine.SpritesDraw(
                texture: spriteTex,
                texture2: 0,
                shader: 0,
                blend: 1,
                stage: 0,
                materialParams: default,
                instances: b,
                count: 1
            );
            // Scene stage, CUSTOM shader (params tint green-ish); falls back to default if rejected.
            ReadOnlySpan<float> c =
                [1.6f, 1.0f, 0f, 0f, 1.2f, 1.2f, 0f, 0f, 1f, 1f, 1f, 1f, 1f, 1f, 0f, 0f];
            ReadOnlySpan<float> prms = [0.3f, 1.0f, 0.3f, 1.0f];
            app.Engine.SpritesDraw(
                texture: spriteTex,
                texture2: 0,
                shader: spriteShader,
                blend: 0,
                stage: 0,
                materialParams: prms,
                instances: c,
                count: 1
            );
            // Overlay stage (pixel space, origin top-left): exact-color square at (40, 40)–(140, 140).
            // Rounded + stroked, exercising the new shape floats: radius 24 px, 6 px border.
            ReadOnlySpan<float> d =
                [90f, 90f, 0f, 0f, 100f, 100f, 0f, 0f, 1f, 1f, 1f, 1f, 1f, 1f, 24f, 6f];
            app.Engine.SpritesDraw(
                texture: spriteTex,
                texture2: 0,
                shader: 0,
                blend: 0,
                stage: 1,
                materialParams: default,
                instances: d,
                count: 1
            );
        }

        if (gpuEmitter != null)
        {
            const float dtSim = 1f / 60f;
            int spawn = gpuEmitter.Step(dtSim);
            app.Engine.ParticlesComputeEmit(
                nodeHandle: 2,
                paramsData: gpuEmitter.BuildParams(spawnCount: spawn, dt: dtSim),
                capacity: gpuEmitter.Capacity,
                blend: gpuEmitter.Blend
            );
        }

        if (scene)
        {
            app.Engine.Render3D(
                width: w,
                height: h
            ); // fill the offscreen 3D target (what ZIGOTE_SHOT dumps)
        }

        app.Frame(); // 2D frame: BeginFrame → RenderFrameV2 (capture) → EndFrame
    }

    sw.Stop();
    if (scene && rendered > 0)
    {
        Console.WriteLine(
            $"[smoke] timing ({(perFrameSet ? "perframe-set" : "baseline")}): {rendered} frames in " +
            $"{sw.ElapsedMilliseconds} ms ({(double)sw.ElapsedMilliseconds / rendered:F2} ms/frame)"
        );
    }

    // 2D paint-throughput benchmark (ZIGOTE_SMOKE_PAINT=<rects>). Builds a synthetic paint list of
    // N rounded rects + text runs ONCE, then submits and renders it repeatedly, reporting the median
    // frame time. This measures the native 2D path end to end — the ZgPaintCommand marshal,
    // fillPaintList's translation, CPU tessellation, the vertex uploads and the draw — which is
    // exactly the path docs/v2-design.md §3 makes claims about and which nothing here could measure.
    // The paint list is rebuilt each frame (as a real app's would be) so tessellation is not
    // accidentally amortised.
    string? paintBench = Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_PAINT");
    if (!scene && !string.IsNullOrEmpty(paintBench))
    {
        int rects = int.TryParse(s: paintBench, result: out int n) && n > 0 ? n : 2000;
        int benchFrames = int.TryParse(
            s: Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_PAINT_FRAMES"),
            result: out int bf
        ) && bf > 0
            ? bf
            : 120;

        var benchPaint = new PaintList();
        var samples = new List<double>(benchFrames);
        var swBench = new System.Diagnostics.Stopwatch();

        // Without this the measurement is a vsync floor and nothing else: 500 rects and 8000 rects
        // both came out at 6.99 ms/frame, because the present blocks. Uncapped, the number is the
        // work.
        app.Engine.SetVsync(false);

        var buildSamples = new List<double>(benchFrames);
        var submitSamples = new List<double>(benchFrames);
        var swBuild = new System.Diagnostics.Stopwatch();
        for (int frame = 0; frame < benchFrames; frame++)
        {
            swBench.Restart();
            swBuild.Restart();
            benchPaint.Clear();
            benchPaint.AddRect(
                bounds: new Rect(x: 0, y: 0, width: w, height: h),
                color: ThemeData.Dark.Background
            );
            for (int i = 0; i < rects; i++)
            {
                // Deterministic spread; a little per-frame motion so nothing can be cached away.
                float fx = (i * 37 % (int)(w - 40)) + (frame % 3);
                float fy = (i * 71 % (int)(h - 40));
                benchPaint.AddRect(
                    bounds: new Rect(x: fx, y: fy, width: 24f, height: 18f),
                    color: new Color(
                        r: (i & 7) / 7f,
                        g: (i >> 3 & 7) / 7f,
                        b: (i >> 6 & 7) / 7f
                    ),
                    radius: (i % 5) * 2f
                );
            }

            swBuild.Stop();
            app.Engine.BeginFrame(1f / 60f);
            // Split the two native halves: SubmitPaintCommands is the ZgPaintCommand marshal plus
            // fillPaintList's translation into the internal command union (design doc P7);
            // FrameEnd is tessellation, vertex upload, draw and present.
            var swSubmit = System.Diagnostics.Stopwatch.StartNew();
            app.Engine.SubmitPaintCommands(benchPaint);
            swSubmit.Stop();
            app.Engine.FrameEnd();
            swBench.Stop();
            samples.Add(swBench.Elapsed.TotalMilliseconds);
            buildSamples.Add(swBuild.Elapsed.TotalMilliseconds);
            submitSamples.Add(swSubmit.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        buildSamples.Sort();
        submitSamples.Sort();
        double median = samples[samples.Count / 2];
        double p95 = samples[(int)(samples.Count * 0.95)];
        double buildMedian = buildSamples[buildSamples.Count / 2];
        double submitMedian = submitSamples[submitSamples.Count / 2];
        double usPerCommand = median * 1000.0 / benchPaint.Count;
        Console.WriteLine(
            FormattableString.Invariant(
                $"[smoke] paint bench: {rects} rects x {benchFrames} frames — median {median:F3} ms/frame (C# build {buildMedian:F3} | native submit/transcode {submitMedian:F3} | native render {median - buildMedian - submitMedian:F3}), {benchPaint.Count} commands ({usPerCommand:F2} us/cmd)"
            )
        );
    }

    // 2D golden-image capture (ZIGOTE_SHOT in non-scene mode): submit a deterministic paint list —
    // exercising the common CMD_RECT / CMD_BORDER commands through the ZgPaintCommand FFI struct — and
    // dump the offscreen 2D render to a BMP. This is the 2D counterpart of the 3D ZIGOTE_SHOT path and
    // the regression seam the 2D paint ABI otherwise lacks (diff with tools/bmpdiff.py).
    string? uiShot = Environment.GetEnvironmentVariable("ZIGOTE_SHOT");
    if (!scene && !string.IsNullOrEmpty(uiShot))
    {
        // ZIGOTE_SMOKE_XFORM wraps the golden scene in a native transform scope
        // (CMD_TRANSFORM_PUSH/POP): "identity" must render byte-identical to no transform at all
        // (the bmpdiff gate for the transform stack); "spin" rotates+scales for a visual check.
        string? xformMode = Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_XFORM");
        var paint = new PaintList();
        if (xformMode == "identity")
            paint.PushTransform(Matrix2D.Identity);
        else if (xformMode == "spin")
        {
            paint.PushTransform(
                Matrix2D.Translation(dx: w * 0.5f, dy: h * 0.5f)
                * Matrix2D.Rotation(0.35f)
                * Matrix2D.Scale(sx: 0.8f, sy: 0.8f)
                * Matrix2D.Translation(dx: -w * 0.5f, dy: -h * 0.5f)
            );
        }

        paint.AddRect(
            bounds: new Rect(
                x: 0,
                y: 0,
                width: w,
                height: h
            ),
            color: ThemeData.Dark.Background
        ); // opaque background
        paint.AddRect(
            bounds: new Rect(
                x: 40,
                y: 40,
                width: 160,
                height: 100
            ),
            color: new Color(r: 0.90f, g: 0.20f, b: 0.20f)
        ); // red block
        paint.AddRect(
            bounds: new Rect(
                x: 240,
                y: 120,
                width: 160,
                height: 160
            ),
            color: new Color(r: 0.20f, g: 0.80f, b: 0.30f),
            radius: 16f
        ); // green rounded
        paint.AddBorder(
            bounds: new Rect(
                x: 430,
                y: 220,
                width: 170,
                height: 150
            ),
            color: new Color(r: 0.30f, g: 0.55f, b: 1f),
            radius: 12f,
            width: 6f
        ); // blue border
        if (xformMode is "identity" or "spin")
            paint.PopTransform();
        app.Engine.SubmitPaintCommands(paint);
        bool okShot = app.Engine.CaptureUiBmp(path: uiShot, width: w, height: h);
        Console.WriteLine($"[smoke] ui-capture {(okShot ? "ok" : "FAILED")} -> {uiShot}");
        if (!okShot) return 2;
    }

    // Clip-coverage capture (ZIGOTE_SMOKE_CLIP=/path/out.bmp, non-scene mode): the same golden seam
    // but exercising CMD_CLIP_START across all three UI pipelines — shape, text (glyph atlas) and
    // image — with content deliberately overhanging the clip rect on every side.
    // ZIGOTE_SMOKE_CLIP_RADIUS=<r> rounds the clip corners; unset/0 = plain rect clip, so a radius-0
    // run must stay byte-identical across native changes to the clip path (tools/bmpdiff.py).
    string? clipShot = Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_CLIP");
    if (!scene && !string.IsNullOrEmpty(clipShot))
    {
        float radius = 0f;
        if (float.TryParse(
                s: Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_CLIP_RADIUS"),
                style: NumberStyles.Float,
                provider: CultureInfo.InvariantCulture,
                result: out float r
            ))
            radius = r;

        // 64×64 checkerboard so the image pipeline's clipped corners are unambiguous in the dump.
        const int checker = 64;
        byte[] pixels = new byte[checker * checker * 4];
        for (int py = 0; py < checker; py++)
        for (int px = 0; px < checker; px++)
        {
            bool on = ((px / 8) + (py / 8)) % 2 == 0;
            int i = ((py * checker) + px) * 4;
            pixels[i + 0] = on ? (byte)255 : (byte)40;
            pixels[i + 1] = on ? (byte)200 : (byte)40;
            pixels[i + 2] = on ? (byte)60 : (byte)40;
            pixels[i + 3] = 255;
        }

        var paint = new PaintList();
        paint.AddRect(
            bounds: new Rect(
                x: 0,
                y: 0,
                width: w,
                height: h
            ),
            color: ThemeData.Dark.Background
        ); // opaque background
        paint.AddClipStart(
            bounds: new Rect(
                x: 120,
                y: 90,
                width: 400,
                height: 300
            ),
            radius: radius
        );
        paint.AddRect(
            bounds: new Rect(
                x: 100,
                y: 70,
                width: 440,
                height: 340
            ),
            color: new Color(r: 0.85f, g: 0.35f, b: 0.15f)
        ); // overhangs all sides
        paint.AddRect(
            bounds: new Rect(
                x: 120,
                y: 90,
                width: 120,
                height: 80
            ),
            color: new Color(r: 0.20f, g: 0.55f, b: 0.95f),
            radius: 10f
        ); // hugs the clip's top-left corner
        paint.AddText(
            text: "Rounded clip coverage",
            baselineX: 90f,
            baselineY: 130f,
            color: new Color(r: 0.95f, g: 0.95f, b: 0.95f),
            fontSize: 22f
        ); // enters through the left edge
        paint.AddText(
            text: "corner glyphs",
            baselineX: 430f,
            baselineY: 380f,
            color: new Color(r: 0.95f, g: 0.90f, b: 0.30f),
            fontSize: 20f
        ); // exits through the bottom-right corner
        paint.AddImage(
            bounds: new Rect(
                x: 460,
                y: 330,
                width: checker,
                height: checker
            ),
            pixelWidth: checker,
            pixelHeight: checker,
            pixels: pixels
        ); // straddles the bottom-right corner
        paint.AddClipEnd();
        paint.AddRect(
            bounds: new Rect(
                x: 20,
                y: 400,
                width: 80,
                height: 60
            ),
            color: new Color(r: 0.45f, g: 0.85f, b: 0.45f),
            radius: 8f
        ); // unclipped control
        app.Engine.SubmitPaintCommands(paint);
        bool okClip = app.Engine.CaptureUiBmp(path: clipShot, width: w, height: h);
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
    string? bidiShot = Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_BIDI");
    if (!scene && !string.IsNullOrEmpty(bidiShot))
    {
        string? bidiFamily = null;
        string? bidiFont = Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_BIDI_FONT");
        if (!string.IsNullOrEmpty(bidiFont) && File.Exists(bidiFont))
        {
            if (app.Engine.LoadFont(name: "bidi-test", path: bidiFont))
                bidiFamily = "bidi-test";
            else
                Console.WriteLine($"[smoke] bidi-capture: failed to load font {bidiFont}");
        }

        var paint = new PaintList();
        paint.AddRect(
            bounds: new Rect(
                x: 0,
                y: 0,
                width: w,
                height: h
            ),
            color: ThemeData.Dark.Background
        );
        var fg = new Color(r: 0.95f, g: 0.95f, b: 0.95f);
        ReadOnlySpan<string> lines = [
            "Latin control 2026", // pure LTR — must match the pre-bidi renderer
            "مرحبا Zigote مرحبا", // Latin word inside Arabic
            "عام 2026 عام", // multi-digit number inside Arabic
            "التاريخ: 05/07/2026", // RTL label + LTR date
            "שלום Zigote!", // Hebrew + Latin + trailing punctuation
        ];
        float y = 60f;
        foreach (string line in lines)
        {
            paint.AddText(
                text: line,
                baselineX: 40f,
                baselineY: y,
                color: fg,
                fontSize: 24f,
                fontFamily: bidiFamily
            );
            y += 60f;
        }

        app.Engine.SubmitPaintCommands(paint);
        bool okBidi = app.Engine.CaptureUiBmp(path: bidiShot, width: w, height: h);
        Console.WriteLine(
            $"[smoke] bidi-capture {(okBidi ? "ok" : "FAILED")} font={bidiFont ?? "(default)"} -> {bidiShot}"
        );
        if (!okBidi) return 2;
    }

    // Texture lifecycle (ZIGOTE_SMOKE_TEXTURES=1): the check for zigote_release_texture and the
    // CPU-copy drop. Load a batch, paint it (forcing GPU upload), release it, and assert the
    // accounting returns to zero. A leak here is invisible in every other mode until the app dies
    // of it, which is exactly how it went unnoticed before.
    if (Environment.GetEnvironmentVariable("ZIGOTE_SMOKE_TEXTURES") is not null)
    {
        const int batch = 64;
        const uint tw = 256, th = 256;
        byte[] pixels = new byte[tw * th * 4];
        Array.Fill(array: pixels, value: (byte)200);

        ZigoteEngine.GetImageStats(count: out int baseCount, cpuBytes: out _, gpuBytes: out _);
        ulong[] handles = new ulong[batch];
        for (int i = 0; i < batch; i++)
            handles[i] = ZigoteEngine.LoadTextureFromRgba(rgba: pixels, width: tw, height: th);

        // Paint them once so every handle gets a real GPU texture, then run a frame so the
        // end-of-frame drain sees the uploads.
        var texPaint = new PaintList();
        for (int i = 0; i < batch; i++)
        {
            texPaint.AddImage(
                bounds: new Rect(
                    x: i % 8 * 32f,
                    y: i / 8 * 32f,
                    width: 32f,
                    height: 32f
                ),
                pixelWidth: (int)tw,
                pixelHeight: (int)th,
                pixels: null,
                cacheKey: handles[i]
            );
        }

        app.Engine.SubmitPaintCommands(texPaint);
        app.Frame();
        app.Frame();

        ZigoteEngine.GetImageStats(
            count: out int loadedCount,
            cpuBytes: out long loadedCpu,
            gpuBytes: out long loadedGpu
        );
        foreach (ulong handleToFree in handles) ZigoteEngine.ReleaseTexture(handleToFree);
        app.Frame(); // releases are deferred to end-of-frame
        ZigoteEngine.GetImageStats(
            count: out int freedCount,
            cpuBytes: out long freedCpu,
            gpuBytes: out long freedGpu
        );

        bool texOk = loadedCount == baseCount + batch &&
                     loadedGpu >= (long)tw * th * 4 * batch &&
                     loadedCpu == 0 && // CPU copies dropped after upload
                     freedCount == baseCount && freedCpu == 0 && freedGpu == 0;
        Console.WriteLine(
            $"[smoke] textures {(texOk ? "ok" : "FAILED")}: loaded={loadedCount} " +
            $"cpu={loadedCpu} gpu={loadedGpu} → after release count={freedCount} " +
            $"cpu={freedCpu} gpu={freedGpu} (baseline {baseCount})"
        );
        if (!texOk) return 2;
    }

    bool ok = rendered >= frames;
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
