using Xunit;

namespace Biometrics.Tests;

/// <summary>
///     Desktop has no biometrics, so what is checked here is the contract every caller relies
///     on: an unavailable device answers rather than throws, and a bad call is caught early.
/// </summary>
public class BiometricsTests
{
    [Fact]
    public async Task Desktop_AnswersUnavailable_WithoutShowingAnything()
    {
        Assert.False(BiometricsPlugin.Available);
        Assert.Equal(BiometricResult.Unavailable, BiometricsPlugin.Check());
        Assert.Equal(
            BiometricResult.Unavailable,
            await BiometricsPlugin.AuthenticateAsync(
                "Unlock your vault", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReasonIsRequired()
        => await Assert.ThrowsAsync<ArgumentException>(
            () => BiometricsPlugin.AuthenticateAsync("  "));
}
