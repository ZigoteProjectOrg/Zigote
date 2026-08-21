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
        Span<float> dst = stackalloc float[4];
        int n = Sample(time: time, dst: dst);
        if (n < 0)
        {
            // Wider than 4 components (morph weights): allocate — the rare path.
            (var a, var b, float t, bool step) = SurroundingKeys(time);
            if (step || t == 0f) return a.Value;
            float[] wide = new float[a.Value.Length];
            for (int i = 0; i < wide.Length; i++)
                wide[i] = a.Value[i] + ((b.Value[i] - a.Value[i]) * t);
            return wide;
        }

        return dst[..n].ToArray();
    }

    /// <summary>
    ///     Allocation-free sample for the ≤4-component paths (T/R/S): writes into
    ///     <paramref name="dst" /> (at least 4 long) and returns the component count. Returns 0 for
    ///     an empty channel and -1 when the values are wider than 4 (weights) — the caller falls
    ///     back to <see cref="Sample(float)" />. Runs per channel per frame during playback.
    /// </summary>
    public int Sample(float time, Span<float> dst)
    {
        if (Keys.Count == 0) return 0;
        if (Keys[0].Value.Length > 4) return -1;

        (var a, var b, float t, bool step) = SurroundingKeys(time);
        int n = a.Value.Length;
        if (step || t == 0f)
        {
            a.Value.AsSpan().CopyTo(dst);
            return n;
        }

        if (Path == AnimationPath.Rotation && n == 4)
        {
            // Shortest-path normalized lerp of two quaternions [x,y,z,w].
            float[] av = a.Value, bv = b.Value;
            float dot = (av[0] * bv[0]) + (av[1] * bv[1]) + (av[2] * bv[2]) + (av[3] * bv[3]);
            float sign = dot < 0f ? -1f : 1f;
            for (int i = 0; i < 4; i++) dst[i] = av[i] + (((bv[i] * sign) - av[i]) * t);
            float len = MathF.Sqrt(
                (dst[0] * dst[0]) + (dst[1] * dst[1]) + (dst[2] * dst[2]) + (dst[3] * dst[3])
            );
            if (len > 1e-6f)
            {
                for (int i = 0; i < 4; i++)
                    dst[i] /= len;
            }

            return 4;
        }

        for (int i = 0; i < n; i++) dst[i] = a.Value[i] + ((b.Value[i] - a.Value[i]) * t);
        return n;
    }

    // Key pair around `time` plus the interpolation factor; t == 0 means "use a's value as-is".
    // CubicSpline is approximated as linear in this foundation (see ROADMAP.md → Animation).
    private (AnimationKeyframe A, AnimationKeyframe B, float T, bool Step) SurroundingKeys(
        float time)
    {
        if (Keys.Count == 1 || time <= Keys[0].Time) return (Keys[0], Keys[0], 0f, false);
        if (time >= Keys[^1].Time) return (Keys[^1], Keys[^1], 0f, false);

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
        if (Interpolation == AnimationInterp.Step) return (a, b, 0f, true);

        float span = b.Time - a.Time;
        float t = span > 1e-6f ? (time - a.Time) / span : 0f;
        return (a, b, t, false);
    }
}

/// <summary>A named animation clip — a set of channels sharing a timeline.</summary>
public sealed class AnimationClip
{
    private float _duration = -1f;

    public string Name { get; init; } = "";
    public List<AnimationChannel> Channels { get; } = [];

    /// <summary>
    ///     Longest channel's duration, computed on first read (the old LINQ Max allocated a
    ///     delegate + enumerator per player Tick per frame). Call <see cref="InvalidateDuration" />
    ///     after mutating channels/keys post-load.
    /// </summary>
    public float Duration
    {
        get
        {
            if (_duration < 0f)
            {
                float max = 0f;
                foreach (var c in Channels) max = MathF.Max(x: max, y: c.Duration);
                _duration = max;
            }

            return _duration;
        }
    }

    public void InvalidateDuration() => _duration = -1f;

    /// <summary>Sample every channel at <paramref name="time" />, grouped by target node name.</summary>
    public Dictionary<string, TargetPose> Sample(float time)
    {
        var result = new Dictionary<string, TargetPose>();
        SampleInto(time: time, result: result);
        return result;
    }

    /// <summary>
    ///     <see cref="Sample" /> into a caller-owned dictionary (cleared first) — the per-frame
    ///     path, so playback allocates nothing in the steady state.
    /// </summary>
    public void SampleInto(float time, Dictionary<string, TargetPose> result)
    {
        result.Clear();
        Span<float> v = stackalloc float[4];
        foreach (var ch in Channels)
        {
            int n = ch.Sample(time: time, dst: v);
            if (n < 3) continue; // empty, or a weights channel (not applied to TRS poses)
            result.TryGetValue(key: ch.TargetNodeName, value: out var pose);
            pose = ch.Path switch {
                AnimationPath.Translation => pose with {
                    Translation = new Vec3(x: v[0], y: v[1], z: v[2]),
                },
                AnimationPath.Rotation when n == 4 => pose with {
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

    // Reused per-frame pose buffer — see AnimationClip.SampleInto.
    private readonly Dictionary<string, AnimationClip.TargetPose> _poseScratch = new();

    /// <summary>Apply the current pose to a node subtree, matching channels to nodes by name.</summary>
    public void ApplyTo(SceneNode root)
    {
        if (Clip is null) return;
        Clip.SampleInto(time: Time, result: _poseScratch);
        if (_poseScratch.Count == 0) return;
        ApplyRecursive(node: root, poses: _poseScratch);
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
