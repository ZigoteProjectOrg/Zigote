using Zigote.Core.Math3D;
using Zigote.Runtime.Scene;

namespace Zigote.Runtime.Animation;

/// <summary>Which transform component an animation channel drives.</summary>
public enum AnimationPath
{
    Translation,
    Rotation,
    Scale,
    Weights,
}

public enum AnimationInterp
{
    Step,
    Linear,
    CubicSpline,
}

/// <summary>One keyframe: a time and a value (3 floats for T/S, 4 for a rotation quaternion).</summary>
public readonly struct AnimationKeyframe(float time, float[] value)
{
    public float Time { get; } = time;
    public float[] Value { get; } = value;
}

/// <summary>
///     A single animation channel: an ordered list of keyframes driving one transform component of
///     one target node. <see cref="Sample" /> evaluates it at an arbitrary time.
/// </summary>
public sealed class AnimationChannel
{
    public int TargetNodeIndex { get; init; } = -1;
    public string TargetNodeName { get; init; } = "";
    public AnimationPath Path { get; init; }
    public AnimationInterp Interpolation { get; init; }
    public List<AnimationKeyframe> Keys { get; } = [];

    public float Duration => Keys.Count == 0 ? 0f : Keys[^1].Time;

    /// <summary>Sample the channel at <paramref name="time" /> (seconds), clamped to the key range.</summary>
    public float[] Sample(float time)
    {
        if (Keys.Count == 0) return [];
        if (Keys.Count == 1 || time <= Keys[0].Time) return Keys[0].Value;
        if (time >= Keys[^1].Time) return Keys[^1].Value;

        // Binary-search the surrounding key pair.
        int lo = 0;
        int hi = Keys.Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (Keys[mid].Time <= time) lo = mid;
            else hi = mid;
        }

        var a = Keys[lo];
        var b = Keys[hi];
        if (Interpolation == AnimationInterp.Step) return a.Value;

        float span = b.Time - a.Time;
        float t = span > 1e-6f ? (time - a.Time) / span : 0f;
        // CubicSpline is approximated as linear in this foundation (see ROADMAP.md → Animation).
        return Path == AnimationPath.Rotation
            ? Nlerp(a: a.Value, b: b.Value, t: t)
            : Lerp(a: a.Value, b: b.Value, t: t);
    }

    private static float[] Lerp(float[] a, float[] b, float t)
    {
        float[] r = new float[a.Length];
        for (int i = 0; i < a.Length; i++) r[i] = a[i] + ((b[i] - a[i]) * t);
        return r;
    }

    private static float[] Nlerp(float[] a, float[] b, float t)
    {
        // Shortest-path normalized lerp of two quaternions [x,y,z,w].
        float dot = (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]) + (a[3] * b[3]);
        float sign = dot < 0f ? -1f : 1f;
        float[] r = new float[4];
        for (int i = 0; i < 4; i++) r[i] = a[i] + (((b[i] * sign) - a[i]) * t);
        float len = MathF.Sqrt((r[0] * r[0]) + (r[1] * r[1]) + (r[2] * r[2]) + (r[3] * r[3]));
        if (len > 1e-6f)
        {
            for (int i = 0; i < 4; i++)
                r[i] /= len;
        }

        return r;
    }
}

/// <summary>A named animation clip — a set of channels sharing a timeline.</summary>
public sealed class AnimationClip
{
    public string Name { get; init; } = "";
    public List<AnimationChannel> Channels { get; } = [];

    public float Duration => Channels.Count == 0 ? 0f : Channels.Max(c => c.Duration);

    /// <summary>Sample every channel at <paramref name="time" />, grouped by target node name.</summary>
    public Dictionary<string, TargetPose> Sample(float time)
    {
        var result = new Dictionary<string, TargetPose>();
        foreach (var ch in Channels)
        {
            float[] v = ch.Sample(time);
            if (v.Length == 0) continue;
            result.TryGetValue(key: ch.TargetNodeName, value: out var pose);
            pose = ch.Path switch {
                AnimationPath.Translation => pose with {
                    Translation = new Vec3(x: v[0], y: v[1], z: v[2]),
                },
                AnimationPath.Rotation => pose with {
                    Rotation = new Quat(
                        x: v[0],
                        y: v[1],
                        z: v[2],
                        w: v[3]
                    ),
                },
                AnimationPath.Scale => pose with { Scale = new Vec3(x: v[0], y: v[1], z: v[2]) },
                _ => pose,
            };
            result[ch.TargetNodeName] = pose;
        }

        return result;
    }

    /// <summary>Sampled pose for one target node at a given time (any component may be null).</summary>
    public readonly record struct TargetPose(Vec3? Translation, Quat? Rotation, Vec3? Scale);
}

/// <summary>
///     Plays an <see cref="AnimationClip" />: advance time, loop/seek, and apply the sampled pose to
///     scene nodes (matched by name). Call <see cref="Tick" /> each frame with delta seconds.
/// </summary>
public sealed class AnimationPlayer
{
    public AnimationClip? Clip { get; set; }
    public float Time { get; private set; }
    public bool Playing { get; private set; }
    public bool Loop { get; set; } = true;
    public float Speed { get; set; } = 1f;

    public void Play() => Playing = true;

    public void Pause() => Playing = false;

    public void Stop()
    {
        Playing = false;
        Time = 0f;
    }

    public void Seek(float t) => Time = MathF.Max(x: 0f, y: t);

    public void Tick(float dt)
    {
        if (!Playing || Clip is null) return;
        float dur = Clip.Duration;
        Time += dt * Speed;
        if (dur > 0f && Time > dur) Time = Loop ? Time % dur : dur;
    }

    /// <summary>Apply the current pose to a node subtree, matching channels to nodes by name.</summary>
    public void ApplyTo(SceneNode root)
    {
        if (Clip is null) return;
        var poses = Clip.Sample(Time);
        if (poses.Count == 0) return;
        ApplyRecursive(node: root, poses: poses);
    }

    private static void ApplyRecursive(SceneNode node,
        Dictionary<string, AnimationClip.TargetPose> poses)
    {
        if (poses.TryGetValue(key: node.Name, value: out var pose))
        {
            if (pose.Translation is { } t) node.Position = t;
            if (pose.Rotation is { } r) node.Rotation = r;
            if (pose.Scale is { } s) node.Scale = s;
        }

        foreach (var child in node.Children) ApplyRecursive(node: child, poses: poses);
    }
}
