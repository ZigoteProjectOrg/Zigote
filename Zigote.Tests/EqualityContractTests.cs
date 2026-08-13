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
        Assert.Equal(
            expected: new Vec2(x: 1.5f, y: -2.5f).GetHashCode(),
            actual: new Vec2(x: 1.5f, y: -2.5f).GetHashCode()
        );
        Assert.Equal(
            expected: new Vec3(x: 1f, y: 2f, z: 3f).GetHashCode(),
            actual: new Vec3(x: 1f, y: 2f, z: 3f).GetHashCode()
        );
        Assert.Equal(
            expected: new Vec4(
                x: 1f,
                y: 2f,
                z: 3f,
                w: 4f
            ).GetHashCode(),
            actual: new Vec4(
                x: 1f,
                y: 2f,
                z: 3f,
                w: 4f
            ).GetHashCode()
        );
        Assert.Equal(
            expected: new Color(
                r: 0.2f,
                g: 0.4f,
                b: 0.6f,
                a: 0.8f
            ).GetHashCode(),
            actual: new Color(
                r: 0.2f,
                g: 0.4f,
                b: 0.6f,
                a: 0.8f
            ).GetHashCode()
        );
        Assert.Equal(
            expected: new Rect(
                x: 1f,
                y: 2f,
                width: 3f,
                height: 4f
            ).GetHashCode(),
            actual: new Rect(
                x: 1f,
                y: 2f,
                width: 3f,
                height: 4f
            ).GetHashCode()
        );
        Assert.Equal(
            expected: new Size(width: 3f, height: 4f).GetHashCode(),
            actual: new Size(width: 3f, height: 4f).GetHashCode()
        );
        Assert.Equal(
            expected: new Offset(x: 3f, y: 4f).GetHashCode(),
            actual: new Offset(x: 3f, y: 4f).GetHashCode()
        );
        Assert.Equal(
            expected: EdgeInsets.All(6f).GetHashCode(),
            actual: EdgeInsets.All(6f).GetHashCode()
        );

        Assert.True(new Vec3(x: 1f, y: 2f, z: 3f).Equals(new Vec3(x: 1f, y: 2f, z: 3f)));
        Assert.True(
            new Color(
                r: 0.2f,
                g: 0.4f,
                b: 0.6f,
                a: 0.8f
            ) == new Color(
                r: 0.2f,
                g: 0.4f,
                b: 0.6f,
                a: 0.8f
            )
        );
    }

    [Fact]
    public void Exact_Equality_DistinguishesSubToleranceDifferences()
    {
        // A difference smaller than the old tolerance is now NOT equal (exact), so the type is a sound
        // hash key. (Under the old tolerance-Equals this returned true while the hashes differed.)
        var a = new Vec3(x: 1f, y: 2f, z: 3f);
        var b = new Vec3(x: 1f + 1e-6f, y: 2f, z: 3f); // below the 1e-5 physics tolerance
        Assert.False(a.Equals(b));
        Assert.NotEqual(expected: a.GetHashCode(), actual: b.GetHashCode());

        var c = new Color(
            r: 0.2f,
            g: 0.4f,
            b: 0.6f,
            a: 0.8f
        );
        var d = new Color(
            r: 0.2f + 1e-8f,
            g: 0.4f,
            b: 0.6f,
            a: 0.8f
        ); // below the 1e-7 standard tolerance
        Assert.False(c.Equals(d));
    }

    [Fact]
    public void ApproxEquals_PreservesTolerantComparison()
    {
        // The tolerant behaviour the sync gates / editors rely on now lives on ApproxEquals.
        Assert.True(
            new Vec3(x: 1f, y: 2f, z: 3f).ApproxEquals(new Vec3(x: 1f + 1e-6f, y: 2f, z: 3f))
        );
        Assert.False(new Vec3(x: 1f, y: 2f, z: 3f).ApproxEquals(new Vec3(x: 1.5f, y: 2f, z: 3f)));

        Assert.True(new Vec2(x: 1f, y: 2f).ApproxEquals(new Vec2(x: 1f + 1e-6f, y: 2f)));
        Assert.True(
            new Vec4(
                x: 1f,
                y: 2f,
                z: 3f,
                w: 4f
            ).ApproxEquals(
                new Vec4(
                    x: 1f,
                    y: 2f,
                    z: 3f,
                    w: 4f + 1e-6f
                )
            )
        );

        Assert.True(
            new Color(
                r: 0.2f,
                g: 0.4f,
                b: 0.6f,
                a: 0.8f
            ).ApproxEquals(
                new Color(
                    r: 0.2f + 1e-8f,
                    g: 0.4f,
                    b: 0.6f,
                    a: 0.8f
                )
            )
        );
        Assert.True(
            new Rect(
                x: 1f,
                y: 2f,
                width: 3f,
                height: 4f
            ).ApproxEquals(
                new Rect(
                    x: 1f + 1e-8f,
                    y: 2f,
                    width: 3f,
                    height: 4f
                )
            )
        );
        Assert.True(
            new Size(width: 3f, height: 4f).ApproxEquals(new Size(width: 3f + 1e-8f, height: 4f))
        );
        Assert.True(new Offset(x: 3f, y: 4f).ApproxEquals(new Offset(x: 3f + 1e-8f, y: 4f)));
        Assert.True(
            EdgeInsets.All(6f).ApproxEquals(
                new EdgeInsets(
                    left: 6f + 1e-8f,
                    top: 6f,
                    right: 6f,
                    bottom: 6f
                )
            )
        );
        Assert.True(
            Constraints.Tight(width: 100f, height: 200f)
                .ApproxEquals(Constraints.Tight(width: 100f + 1e-8f, height: 200f))
        );
    }

    [Fact]
    public void UsableAsDictionaryKey()
    {
        // The concrete payoff: exact-equal keys resolve to the same bucket.
        var map = new Dictionary<Vec3, string> { [new Vec3(x: 1f, y: 2f, z: 3f)] = "a" };
        Assert.Equal(expected: "a", actual: map[new Vec3(x: 1f, y: 2f, z: 3f)]);
        Assert.False(map.ContainsKey(new Vec3(x: 1f, y: 2f, z: 3.0001f)));
    }
}
