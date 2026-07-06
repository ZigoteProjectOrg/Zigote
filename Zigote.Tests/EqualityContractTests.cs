using Xunit;
using Zigote.Core;
using Zigote.Core.Math3D;

namespace Zigote.Tests;

/// <summary>
///     Guards the Equals/GetHashCode contract on the core value types. They previously paired a
///     tolerance-based Equals with an exact GetHashCode — a contract violation (equal values could
///     hash
///     differently) that made them unsafe as dictionary/set keys and non-transitive. Equals is now
///     EXACT (consistent with the hash); the tolerant comparison moved to ApproxEquals.
/// </summary>
public class EqualityContractTests
{
    [Fact]
    public void Equal_Values_HaveEqualHashCodes()
    {
        // The contract: a.Equals(b) ⟹ a.GetHashCode() == b.GetHashCode().
        Assert.Equal(new Vec2(1.5f, -2.5f).GetHashCode(), new Vec2(1.5f, -2.5f).GetHashCode());
        Assert.Equal(new Vec3(1f, 2f, 3f).GetHashCode(), new Vec3(1f, 2f, 3f).GetHashCode());
        Assert.Equal(
            new Vec4(
                1f,
                2f,
                3f,
                4f
            ).GetHashCode(),
            new Vec4(
                1f,
                2f,
                3f,
                4f
            ).GetHashCode()
        );
        Assert.Equal(
            new Color(
                0.2f,
                0.4f,
                0.6f,
                0.8f
            ).GetHashCode(),
            new Color(
                0.2f,
                0.4f,
                0.6f,
                0.8f
            ).GetHashCode()
        );
        Assert.Equal(
            new Rect(
                1f,
                2f,
                3f,
                4f
            ).GetHashCode(),
            new Rect(
                1f,
                2f,
                3f,
                4f
            ).GetHashCode()
        );
        Assert.Equal(new Size(3f, 4f).GetHashCode(), new Size(3f, 4f).GetHashCode());
        Assert.Equal(new Offset(3f, 4f).GetHashCode(), new Offset(3f, 4f).GetHashCode());
        Assert.Equal(EdgeInsets.All(6f).GetHashCode(), EdgeInsets.All(6f).GetHashCode());

        Assert.True(new Vec3(1f, 2f, 3f).Equals(new Vec3(1f, 2f, 3f)));
        Assert.True(
            new Color(
                0.2f,
                0.4f,
                0.6f,
                0.8f
            ) == new Color(
                0.2f,
                0.4f,
                0.6f,
                0.8f
            )
        );
    }

    [Fact]
    public void Exact_Equality_DistinguishesSubToleranceDifferences()
    {
        // A difference smaller than the old tolerance is now NOT equal (exact), so the type is a sound
        // hash key. (Under the old tolerance-Equals this returned true while the hashes differed.)
        var a = new Vec3(1f, 2f, 3f);
        var b = new Vec3(1f + 1e-6f, 2f, 3f); // below the 1e-5 physics tolerance
        Assert.False(a.Equals(b));
        Assert.NotEqual(a.GetHashCode(), b.GetHashCode());

        var c = new Color(
            0.2f,
            0.4f,
            0.6f,
            0.8f
        );
        var d = new Color(
            0.2f + 1e-8f,
            0.4f,
            0.6f,
            0.8f
        ); // below the 1e-7 standard tolerance
        Assert.False(c.Equals(d));
    }

    [Fact]
    public void ApproxEquals_PreservesTolerantComparison()
    {
        // The tolerant behaviour the sync gates / editors rely on now lives on ApproxEquals.
        Assert.True(new Vec3(1f, 2f, 3f).ApproxEquals(new Vec3(1f + 1e-6f, 2f, 3f)));
        Assert.False(new Vec3(1f, 2f, 3f).ApproxEquals(new Vec3(1.5f, 2f, 3f)));

        Assert.True(new Vec2(1f, 2f).ApproxEquals(new Vec2(1f + 1e-6f, 2f)));
        Assert.True(
            new Vec4(
                1f,
                2f,
                3f,
                4f
            ).ApproxEquals(
                new Vec4(
                    1f,
                    2f,
                    3f,
                    4f + 1e-6f
                )
            )
        );

        Assert.True(
            new Color(
                0.2f,
                0.4f,
                0.6f,
                0.8f
            ).ApproxEquals(
                new Color(
                    0.2f + 1e-8f,
                    0.4f,
                    0.6f,
                    0.8f
                )
            )
        );
        Assert.True(
            new Rect(
                1f,
                2f,
                3f,
                4f
            ).ApproxEquals(
                new Rect(
                    1f + 1e-8f,
                    2f,
                    3f,
                    4f
                )
            )
        );
        Assert.True(new Size(3f, 4f).ApproxEquals(new Size(3f + 1e-8f, 4f)));
        Assert.True(new Offset(3f, 4f).ApproxEquals(new Offset(3f + 1e-8f, 4f)));
        Assert.True(
            EdgeInsets.All(6f).ApproxEquals(
                new EdgeInsets(
                    6f + 1e-8f,
                    6f,
                    6f,
                    6f
                )
            )
        );
        Assert.True(
            Constraints.Tight(100f, 200f).ApproxEquals(Constraints.Tight(100f + 1e-8f, 200f))
        );
    }

    [Fact]
    public void UsableAsDictionaryKey()
    {
        // The concrete payoff: exact-equal keys resolve to the same bucket.
        var map = new Dictionary<Vec3, string> { [new Vec3(1f, 2f, 3f)] = "a" };
        Assert.Equal("a", map[new Vec3(1f, 2f, 3f)]);
        Assert.False(map.ContainsKey(new Vec3(1f, 2f, 3.0001f)));
    }
}