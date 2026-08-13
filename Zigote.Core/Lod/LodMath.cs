namespace Zigote.Core.Lod;

/// <summary>
///     Pure level-of-detail selection math, with no scene-graph or native dependency so it is
///     headless-testable. Distances are world units from the camera. These are the rules the editor's
///     <c>LodSystem</c> applies while walking the scene each frame.
/// </summary>
public static class LodMath
{
    /// <summary>
    ///     True when a node with the given <paramref name="maxDistance" /> should be culled at this
    ///     camera <paramref name="distance" />. A <paramref name="maxDistance" /> &lt;= 0 means
    ///     "no distance limit" (never culled by distance).
    /// </summary>
    public static bool CulledByDistance(float maxDistance, float distance)
    {
        return maxDistance > 0f && distance > maxDistance;
    }

    /// <summary>
    ///     Select the active LOD level for a camera at <paramref name="distance" /> among the LOD
    ///     children's max-distance budgets. Author levels near→far (ascending finite budgets, with an
    ///     optional fallback level whose budget is &lt;= 0 meaning "covers any distance", placed last).
    ///     Returns the index of the nearest level whose budget reaches <paramref name="distance" />, else
    ///     the fallback level, else -1 (the whole group is beyond every level → cull it).
    /// </summary>
    public static int SelectLevel(ReadOnlySpan<float> levelMaxDistances, float distance)
    {
        var fallback = -1;
        for (var i = 0; i < levelMaxDistances.Length; i++)
        {
            var d = levelMaxDistances[i];
            if (d <= 0f)
            {
                if (fallback < 0) fallback = i; // first "covers all" level is the fallback
                continue;
            }

            if (distance <= d) return i;
        }

        return fallback;
    }
}
