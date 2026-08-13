using Zigote.Core.Licenses;

namespace Zigote.UI.Licensing;

/// <summary>
///     Registers the bundled fonts' license texts (embedded as resources — the OFL requires the
///     text to accompany the fonts wherever they ship) with the <see cref="LicenseRegistry" />.
///     Called from the <c>App</c> constructor and <c>LicensesView</c>, so any UI app gets the font
///     attributions without opting in; headless consumers can call it explicitly.
/// </summary>
public static class FontLicenses
{
    private static bool _registered;

    public static void EnsureRegistered()
    {
        if (_registered) return;
        _registered = true;
        LicenseRegistry.AddCollector(Create);
    }

    private static IEnumerable<LicenseEntry> Create()
    {
        return [
            new LicenseEntry(
                Component: "Inter (bundled font)",
                License: "SIL Open Font License 1.1",
                Text: Resource("Zigote.UI.Fonts.Inter.OFL")
            ) { Homepage = "https://rsms.me/inter/" },
            new LicenseEntry(
                Component: "Iosevka (bundled font)",
                License: "SIL Open Font License 1.1",
                Text: Resource("Zigote.UI.Fonts.Iosevka.OFL")
            ) { Homepage = "https://typeof.net/Iosevka/" },
            new LicenseEntry(
                Component: "Noto Emoji (bundled font)",
                License: "SIL Open Font License 1.1",
                Text: Resource("Zigote.UI.Fonts.NotoEmoji.OFL")
            ) { Homepage = "https://fonts.google.com/noto/specimen/Noto+Emoji" },
            new LicenseEntry(
                Component: "Material Icons (bundled font)",
                License: "Apache-2.0",
                Text: Resource("Zigote.UI.Fonts.MaterialIcons.LICENSE")
            ) { Homepage = "https://github.com/google/material-design-icons" },
        ];
    }

    private static string Resource(string name)
    {
        using var stream = typeof(FontLicenses).Assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException(
                               $"Missing embedded resource '{name}'."
                           );
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
