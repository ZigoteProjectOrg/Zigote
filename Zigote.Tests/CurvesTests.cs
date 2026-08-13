using Xunit;
using Zigote.Core.Animation;

namespace Zigote.Tests;

/// <summary>
///     Boundary + monotonicity guards for the easing curves. The normalised contract is f(0)=0 and
///     f(1)=1; overshooting curves (Spring/EaseOutBack/Bounce/Elastic) are easy to break silently at
///     the endpoints when their constants are tweaked.
/// </summary>
public class CurvesTests
{
    private const float Eps = 1e-3f;

    public static IEnumerable<object[]> AllCurves()
    {
        yield return [nameof(Curves.Linear), (Func<float, float>)Curves.Linear];
        yield return [nameof(Curves.EaseIn), (Func<float, float>)Curves.EaseIn];
        yield return [nameof(Curves.EaseOut), (Func<float, float>)Curves.EaseOut];
        yield return [nameof(Curves.EaseInOut), (Func<float, float>)Curves.EaseInOut];
        yield return [nameof(Curves.BounceOut), (Func<float, float>)Curves.BounceOut];
        yield return [nameof(Curves.ElasticOut), (Func<float, float>)Curves.ElasticOut];
        yield return [nameof(Curves.Spring), (Func<float, float>)Curves.Spring];
        yield return [nameof(Curves.EaseOutBack), (Func<float, float>)Curves.EaseOutBack];
    }

    [Theory]
    [MemberData(nameof(AllCurves))]
    public void Endpoints_AreNormalised(string name, Func<float, float> curve)
    {
        Assert.True(
            condition: MathF.Abs(curve(0f) - 0f) < Eps,
            userMessage: $"{name}(0) = {curve(0f)}"
        );
        Assert.True(
            condition: MathF.Abs(curve(1f) - 1f) < Eps,
            userMessage: $"{name}(1) = {curve(1f)}"
        );
    }

    [Theory]
    [MemberData(nameof(AllCurves))]
    public void StaysFinite_AcrossDomain(string name, Func<float, float> curve)
    {
        for (float t = 0f; t <= 1f; t += 0.05f)
        {
            Assert.True(
                condition: float.IsFinite(curve(t)),
                userMessage: $"{name}({t}) = {curve(t)}"
            );
        }
    }

    [Theory]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(0.75f)]
    public void Linear_IsIdentity(float t) => Assert.Equal(
        expected: t,
        actual: Curves.Linear(t),
        precision: 5
    );

    [Fact]
    public void EaseInOut_IsSymmetricAtMidpoint() =>
        Assert.True(MathF.Abs(Curves.EaseInOut(0.5f) - 0.5f) < Eps);

    [Fact]
    public void MonotonicEases_AreIncreasing()
    {
        foreach (var curve in new[] {
                     Curves.Linear,
                     Curves.EaseIn,
                     Curves.EaseOut,
                     Curves.EaseInOut,
                 })
        {
            float prev = curve(0f);
            for (float t = 0.05f; t <= 1f; t += 0.05f)
            {
                float cur = curve(t);
                Assert.True(
                    condition: cur >= prev - Eps,
                    userMessage: $"non-monotonic at {t}: {cur} < {prev}"
                );
                prev = cur;
            }
        }
    }
}
