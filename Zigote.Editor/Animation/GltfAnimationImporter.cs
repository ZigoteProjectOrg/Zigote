using Zigote.Editor.Scene;
using Zigote.Runtime.Animation;

namespace Zigote.Editor.Animation;

/// <summary>
///     Builds <see cref="AnimationClip" />s from the native import manifest's animation data:
///     per-node translation/rotation/scale keyframe channels. Assimp resamples sources to LINEAR
///     keys; channels target nodes by name, matching the hierarchy-preserving import path in
///     <see cref="GltfLoader" />.
/// </summary>
public static class GltfAnimationImporter
{
    internal static List<AnimationClip> Import(IReadOnlyList<ModelAnimation> animations)
    {
        var clips = new List<AnimationClip>();
        foreach (var anim in animations)
        {
            var clip = new AnimationClip { Name = anim.Name };

            foreach (var ch in anim.Channels)
            {
                if (!TryPath(ch.Path, out var path)) continue;

                var stride = path == AnimationPath.Rotation ? 4 : 3;
                if (ch.Times.Length == 0 || ch.Values.Length < ch.Times.Length * stride) continue;

                var interp = string.Equals(
                    ch.Interpolation,
                    "STEP",
                    StringComparison.OrdinalIgnoreCase
                )
                    ? AnimationInterp.Step
                    : AnimationInterp.Linear;

                var channel = new AnimationChannel {
                    TargetNodeName = ch.Node,
                    Path = path,
                    Interpolation = interp,
                };

                for (var k = 0; k < ch.Times.Length; k++)
                {
                    var value = new float[stride];
                    Array.Copy(
                        ch.Values,
                        k * stride,
                        value,
                        0,
                        stride
                    );
                    channel.Keys.Add(new AnimationKeyframe(ch.Times[k], value));
                }

                clip.Channels.Add(channel);
            }

            if (clip.Channels.Count > 0) clips.Add(clip);
        }

        return clips;
    }

    private static bool TryPath(string s, out AnimationPath path)
    {
        switch (s)
        {
            case "translation":
                path = AnimationPath.Translation;
                return true;
            case "rotation":
                path = AnimationPath.Rotation;
                return true;
            case "scale":
                path = AnimationPath.Scale;
                return true;
            default:
                path = AnimationPath.Translation;
                return false;
        }
    }
}