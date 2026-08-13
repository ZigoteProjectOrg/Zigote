using Xunit;
using Zigote.Core.Licenses;
using Zigote.UI.Licensing;

namespace Zigote.Tests;

/// <summary>
///     The license registry backs every app's open-source-attributions screen
///     (LicenseRegistry.BuildText / the LicensesView widget).
/// </summary>
public class LicenseRegistryTests
{
    [Fact]
    public void Collect_ContainsFrameworkAndNativeEntries()
    {
        var entries = LicenseRegistry.Collect();

        Assert.Contains(entries, e => e is { Component: "Zigote", License: "MIT" });
        Assert.Contains(entries, e => e.Component == "SDL3");
        Assert.Contains(entries, e => e.Component == "FreeType");
        Assert.Contains(entries, e => e.Component == "wgpu-native");
        Assert.Contains(entries, e => e.Component == "meshoptimizer");
    }

    [Fact]
    public void FontLicenses_RegisterEmbeddedTexts()
    {
        FontLicenses.EnsureRegistered();
        var entries = LicenseRegistry.Collect();

        var inter = Assert.Single(entries, e => e.Component.StartsWith("Inter"));
        Assert.Contains("SIL OPEN FONT LICENSE", inter.Text);
        var icons = Assert.Single(entries, e => e.Component.StartsWith("Material Icons"));
        Assert.Contains("Apache License", icons.Text);
        Assert.Contains(
            entries,
            e => e.Component.StartsWith("Iosevka") && e.Text.Contains("Renzhi Li")
        );
        Assert.Contains(entries, e => e.Component.StartsWith("Noto Emoji"));
    }

    [Fact]
    public void BuildText_RendersAppEntriesAndAttributions()
    {
        LicenseRegistry.Add(
            new LicenseEntry("MyTestGame", "Proprietary", "All rights reserved (test).")
        );

        var text = LicenseRegistry.BuildText("Open-source licenses");

        Assert.StartsWith("Open-source licenses", text);
        Assert.Contains("Zigote — MIT", text);
        Assert.Contains("MyTestGame — Proprietary", text);
        // The FTL credit line must survive into the rendered document verbatim.
        Assert.Contains("Portions of this software are copyright © The FreeType Project", text);
        Assert.Contains("https://github.com/jrouwe/JoltPhysics", text);
    }
}
