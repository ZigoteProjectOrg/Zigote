using Xunit;
using Zigote.Core.Native;
using Zigote.Runtime.Scene;

namespace Zigote.Tests;

public class ProjectSettingsTests
{
    // The project persists render settings as a native-interop struct of public fields, which only
    // serializes with JsonSerializerOptions.IncludeFields — verify the round-trip actually works.
    [Fact]
    public void RenderSettings_RoundTrip_Through_ProjectFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"zigote_test_{Guid.NewGuid():N}.zigoteproj");
        try
        {
            var project = new ZigoteProject {
                Name = "RoundTrip",
                RenderSettings = new ZgRenderSettings3D {
                    AmbientIntensity = 0.42f,
                    Exposure = 1.23f,
                    DofEnabled = 1f,
                    DofFStop = 4.0f,
                },
            };
            project.Save(path);

            var loaded = ZigoteProject.Load(path);
            Assert.Equal("RoundTrip", loaded.Name);
            Assert.NotNull(loaded.RenderSettings);
            Assert.Equal(0.42f, loaded.RenderSettings!.Value.AmbientIntensity);
            Assert.Equal(1.23f, loaded.RenderSettings!.Value.Exposure);
            Assert.Equal(1f, loaded.RenderSettings!.Value.DofEnabled);
            Assert.Equal(4.0f, loaded.RenderSettings!.Value.DofFStop);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // A project that never saved render settings (older project, or a fresh scaffold) loads them as
    // null, so the engine keeps its built-in defaults rather than getting zeroed-out settings applied.
    [Fact]
    public void Project_Without_RenderSettings_Loads_Null()
    {
        var path = Path.Combine(Path.GetTempPath(), $"zigote_test_{Guid.NewGuid():N}.zigoteproj");
        try
        {
            new ZigoteProject { Name = "NoSettings" }.Save(path);

            var loaded = ZigoteProject.Load(path);
            Assert.Equal("NoSettings", loaded.Name);
            Assert.Null(loaded.RenderSettings);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}