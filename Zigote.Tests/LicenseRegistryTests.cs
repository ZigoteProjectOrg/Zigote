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

        Assert.Contains(
            collection: entries,
            filter: e => e is { Component: "Zigote", License: "MIT" }
        );
        Assert.Contains(collection: entries, filter: e => e.Component == "SDL3");
        Assert.Contains(collection: entries, filter: e => e.Component == "FreeType");
        Assert.Contains(collection: entries, filter: e => e.Component == "wgpu-native");
        Assert.Contains(collection: entries, filter: e => e.Component == "meshoptimizer");
    }

    [Fact]
    public void FontLicenses_RegisterEmbeddedTexts()
    {
        FontLicenses.EnsureRegistered();
        var entries = LicenseRegistry.Collect();

        var inter = Assert.Single(
            collection: entries,
            predicate: e => e.Component.StartsWith("Inter")
        );
        Assert.Contains(expectedSubstring: "SIL OPEN FONT LICENSE", actualString: inter.Text);
        var icons = Assert.Single(
            collection: entries,
            predicate: e => e.Component.StartsWith("Material Icons")
        );
        Assert.Contains(expectedSubstring: "Apache License", actualString: icons.Text);
        Assert.Contains(
            collection: entries,
            filter: e => e.Component.StartsWith("Iosevka") && e.Text.Contains("Renzhi Li")
        );
        Assert.Contains(collection: entries, filter: e => e.Component.StartsWith("Noto Emoji"));
    }

    [Fact]
    public void BuildText_RendersAppEntriesAndAttributions()
    {
        LicenseRegistry.Add(
            new LicenseEntry(
                Component: "MyTestGame",
                License: "Proprietary",
                Text: "All rights reserved (test)."
            )
        );

        string text = LicenseRegistry.BuildText("Open-source licenses");

        Assert.StartsWith(expectedStartString: "Open-source licenses", actualString: text);
        Assert.Contains(expectedSubstring: "Zigote — MIT", actualString: text);
        Assert.Contains(expectedSubstring: "MyTestGame — Proprietary", actualString: text);
        // The FTL credit line must survive into the rendered document verbatim.
        Assert.Contains(
            expectedSubstring: "Portions of this software are copyright © The FreeType Project",
            actualString: text
        );
        Assert.Contains(
            expectedSubstring: "https://github.com/jrouwe/JoltPhysics",
            actualString: text
        );
    }
}
