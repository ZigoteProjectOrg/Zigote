using System.Text.Json;
using System.Text.Json.Serialization;
using Zigote.Core;
using Zigote.Core.Math3D;

namespace Zigote.Vfx;

/// <summary>
///     Stable JSON codec for <see cref="VfxEmitterAsset" /> — the baked form an exported game ships
///     instead of the node graph. A flat DTO with a <see cref="VfxModuleKind" /> discriminator (no
///     polymorphic serialization) keeps it source-gen friendly, so it works under NativeAOT.
/// </summary>
public static class VfxAssetJson
{
    public static string Serialize(VfxEmitterAsset a)
    {
        var dto = new VfxAssetDto {
            Capacity = a.Capacity,
            Looping = a.Looping,
            Duration = a.Duration,
            Space = (int)a.Space,
            Seed = a.Seed,
            SpawnRate = a.SpawnRate,
            Bursts = a.Bursts.Count == 0
                ? null
                : a.Bursts.Select(b => new[] {
                        b.Time,
                        b.Count,
                    }
                ).ToList(),
            Shape = (int)a.Shape,
            ShapeRadius = a.ShapeRadius,
            ShapeBox = Arr(a.ShapeBoxHalfExtents),
            ConeAngle = a.ConeAngleDegrees,
            EmitDirection = Arr(a.EmitDirection),
            StartLifetime = Arr(a.StartLifetime),
            StartSpeed = Arr(a.StartSpeed),
            StartSize = Arr(a.StartSize),
            StartRotation = Arr(a.StartRotation),
            StartAngularVelocity = Arr(a.StartAngularVelocity),
            StartColor = Arr(a.StartColor),
            StartColorVariation = Arr(a.StartColorVariation),
            Modules =
                a.UpdateModules.Count == 0 ? null : a.UpdateModules.Select(ModuleDto).ToList(),
            Blend = (int)a.Blend,
            TexturePath = a.TexturePath,
            SoftParticles = a.SoftParticles,
        };
        return JsonSerializer.Serialize(dto, VfxJsonContext.Default.VfxAssetDto);
    }

    public static VfxEmitterAsset Deserialize(string json)
    {
        var dto = JsonSerializer.Deserialize(json, VfxJsonContext.Default.VfxAssetDto)
                  ?? throw new InvalidDataException("Empty VFX asset JSON.");
        var a = new VfxEmitterAsset {
            Capacity = dto.Capacity,
            Looping = dto.Looping,
            Duration = dto.Duration,
            Space = (SimulationSpace)dto.Space,
            Seed = dto.Seed,
            SpawnRate = dto.SpawnRate,
            Shape = (EmissionShape)dto.Shape,
            ShapeRadius = dto.ShapeRadius,
            ShapeBoxHalfExtents = Vec(dto.ShapeBox),
            ConeAngleDegrees = dto.ConeAngle,
            EmitDirection = Vec(dto.EmitDirection),
            StartLifetime = Range(dto.StartLifetime),
            StartSpeed = Range(dto.StartSpeed),
            StartSize = Range(dto.StartSize),
            StartRotation = Range(dto.StartRotation),
            StartAngularVelocity = Range(dto.StartAngularVelocity),
            StartColor = Col(dto.StartColor),
            StartColorVariation = Col(dto.StartColorVariation),
            Blend = (VfxBlendMode)dto.Blend,
            TexturePath = dto.TexturePath,
            SoftParticles = dto.SoftParticles,
        };
        if (dto.Bursts is not null)
            foreach (var b in dto.Bursts)
                a.Bursts.Add(new VfxBurst(b[0], (int)b[1]));
        if (dto.Modules is not null)
            foreach (var m in dto.Modules)
                a.UpdateModules.Add(Module(m));
        return a;
    }

    private static VfxModuleDto ModuleDto(VfxUpdateModule m)
    {
        return m switch {
            GravityModule g => new VfxModuleDto {
                Kind = (int)m.Kind,
                Vector = Arr(g.Gravity),
            },
            DragModule d => new VfxModuleDto {
                Kind = (int)m.Kind,
                Scalar = d.Drag,
            },
            TurbulenceModule t => new VfxModuleDto {
                Kind = (int)m.Kind,
                Scalar = t.Strength,
                Scalar2 = t.Frequency,
            },
            VortexModule v => new VfxModuleDto {
                Kind = (int)m.Kind,
                Vector = Arr(v.Axis),
                Scalar = v.Strength,
            },
            ColorOverLifeModule c => new VfxModuleDto {
                Kind = (int)m.Kind,
                Stops = c.Ramp.Stops.Select(s => new[] {
                            s.Position,
                            s.Color.R,
                            s.Color.G,
                            s.Color.B,
                            s.Color.A,
                        }
                    )
                    .ToList(),
            },
            SizeOverLifeModule s => new VfxModuleDto {
                Kind = (int)m.Kind,
                Stops = CurveStops(s.Curve),
            },
            AlphaOverLifeModule al => new VfxModuleDto {
                Kind = (int)m.Kind,
                Stops = CurveStops(al.Curve),
            },
            _ => throw new NotSupportedException($"Unserializable VFX module: {m.GetType().Name}"),
        };
    }

    private static VfxUpdateModule Module(VfxModuleDto m)
    {
        return (VfxModuleKind)m.Kind switch {
            VfxModuleKind.Gravity => new GravityModule(Vec(m.Vector!)),
            VfxModuleKind.Drag => new DragModule(m.Scalar ?? 0f),
            VfxModuleKind.Turbulence => new TurbulenceModule(m.Scalar ?? 0f, m.Scalar2 ?? 1f),
            VfxModuleKind.Vortex => new VortexModule(Vec(m.Vector!), m.Scalar ?? 0f),
            VfxModuleKind.ColorOverLife => new ColorOverLifeModule(
                new ColorRamp(
                    m.Stops!.Select(s => new ColorStop(
                            s[0],
                            new Color(
                                s[1],
                                s[2],
                                s[3],
                                s[4]
                            )
                        )
                    )
                )
            ),
            VfxModuleKind.SizeOverLife => new SizeOverLifeModule(Curve(m.Stops!)),
            VfxModuleKind.AlphaOverLife => new AlphaOverLifeModule(Curve(m.Stops!)),
            _ => throw new NotSupportedException($"Unknown VFX module kind: {m.Kind}"),
        };
    }

    private static List<float[]> CurveStops(FloatCurve c)
    {
        return c.Keys.Select(k => new[] {
                k.Position,
                k.Value,
            }
        ).ToList();
    }

    private static FloatCurve Curve(List<float[]> stops)
    {
        return new FloatCurve(stops.Select(s => new CurveKey(s[0], s[1])));
    }

    private static float[] Arr(Vec3 v)
    {
        return [v.X, v.Y, v.Z];
    }

    private static float[] Arr(Color c)
    {
        return [c.R, c.G, c.B, c.A];
    }

    private static float[] Arr(FloatRange r)
    {
        return [r.Min, r.Max];
    }

    private static Vec3 Vec(float[] a)
    {
        return new Vec3(a[0], a[1], a[2]);
    }

    private static Color Col(float[] a)
    {
        return new Color(
            a[0],
            a[1],
            a[2],
            a[3]
        );
    }

    private static FloatRange Range(float[] a)
    {
        return new FloatRange(a[0], a[1]);
    }
}

public sealed class VfxAssetDto
{
    public int Capacity { get; set; } = 1024;
    public bool Looping { get; set; } = true;
    public float Duration { get; set; }
    public int Space { get; set; }
    public uint Seed { get; set; }
    public float SpawnRate { get; set; }
    public List<float[]>? Bursts { get; set; }
    public int Shape { get; set; }
    public float ShapeRadius { get; set; }
    public float[] ShapeBox { get; set; } = [0.5f, 0.5f, 0.5f];
    public float ConeAngle { get; set; }
    public float[] EmitDirection { get; set; } = [0f, 1f, 0f];
    public float[] StartLifetime { get; set; } = [1f, 1f];
    public float[] StartSpeed { get; set; } = [0f, 0f];
    public float[] StartSize { get; set; } = [0.1f, 0.1f];
    public float[] StartRotation { get; set; } = [0f, 0f];
    public float[] StartAngularVelocity { get; set; } = [0f, 0f];
    public float[] StartColor { get; set; } = [1f, 1f, 1f, 1f];
    public float[] StartColorVariation { get; set; } = [1f, 1f, 1f, 1f];
    public List<VfxModuleDto>? Modules { get; set; }
    public int Blend { get; set; }
    public string? TexturePath { get; set; }
    public bool SoftParticles { get; set; } = true;
}

public sealed class VfxModuleDto
{
    public int Kind { get; set; }
    public float[]? Vector { get; set; }
    public float? Scalar { get; set; }
    public float? Scalar2 { get; set; }
    public List<float[]>? Stops { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(VfxAssetDto))]
internal partial class VfxJsonContext : JsonSerializerContext;