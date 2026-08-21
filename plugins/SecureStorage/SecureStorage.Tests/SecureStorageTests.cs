using Xunit;

namespace SecureStorage.Tests;

/// <summary>
///     Key validation and the desktop plumbing that decides where a secret goes. The keystores
///     themselves are not touched: a test that wrote to the developer's login keyring would be a
///     surprise, and the round trip is the OS's code, not this plugin's.
/// </summary>
public class SecureStorageTests
{
    [Theory]
    [InlineData("refresh.token")]
    [InlineData("user_1-key")]
    public void Validate_AcceptsPlainKeys(string key) => SecureStoragePlugin.Validate(key);

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("has space")]
    [InlineData("path/traversal")]
    [InlineData("..")]
    [InlineData("new\nline")]
    public void Validate_RejectsAnythingThatWouldEscapeAKeyName(string key)
        => Assert.Throws<ArgumentException>(() => SecureStoragePlugin.Validate(key));

    [Fact]
    public void Validate_RejectsOverlongKeys()
        => Assert.Throws<ArgumentException>(() => SecureStoragePlugin.Validate(new string('k', 129)));

    [Fact]
    public void Service_DefaultsToTheApp_AndRejectsBlank()
    {
        Assert.False(string.IsNullOrWhiteSpace(SecureStoragePlugin.Service));
        Assert.Throws<ArgumentException>(() => SecureStoragePlugin.Service = " ");
    }

    [Fact]
    public void WindowsFilePath_StaysUnderTheServiceFolder()
    {
        string path = SecureStorageDriver.FilePath("MyApp", "refresh.token");
        Assert.EndsWith(Path.Combine("MyApp", "secrets", "refresh.token.bin"), path);
    }

    [Fact]
    public void Which_FindsSomethingEveryPosixBoxHas_AndNothingInvented()
    {
        Assert.Null(SecureStorageDriver.Which("definitely-not-a-real-tool-zzz"));
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            Assert.NotNull(SecureStorageDriver.Which("sh"));
    }
}
