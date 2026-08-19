using DeviceInfo;
using Zigote.UI.Adwaita;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace DeviceInfoExample;

/// <summary>
///     Every <see cref="DeviceProfile" /> field as a GNOME Settings-style "About" page:
///     an Adwaita headerbar over a preferences page of grouped rows, value in the suffix.
/// </summary>
public sealed class DeviceInfoExampleApp : AdwaitaApp
{
    public DeviceInfoExampleApp() : base(home: new SafeArea(new DeviceInfoPage()), title: "Device Info")
    {
        Width = 560;
        Height = 640;
    }
}

internal sealed class DeviceInfoPage : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        var p = DeviceInfoPlugin.Get();

        return new AdwToolbarView(
            new AdwPreferencesPage {
                Groups = {
                    new AdwPreferencesGroup(title: "System") {
                        Rows = {
                            Fact(title: "Operating System", value: p.Os),
                            Fact(title: "OS Version", value: p.OsVersion),
                            Fact(title: "Android SDK", value: p.SdkInt == 0 ? "" : p.SdkInt.ToString()),
                            Fact(title: "Architecture", value: p.Architecture),
                        },
                    },
                    new AdwPreferencesGroup(
                        title: "Hardware",
                        description: "DMI on Linux PCs, device-tree on ARM boards; empty where the platform keeps quiet."
                    ) {
                        Rows = {
                            Fact(title: "Model", value: p.Model),
                            Fact(title: "Manufacturer", value: p.Manufacturer),
                            Fact(title: "Physical Device", value: p.IsPhysicalDevice ? "Yes" : "No (emulator)"),
                        },
                    },
                    new AdwPreferencesGroup(
                        title: "Identity",
                        description: "Stable device identifier — machine-id, MachineGuid, ANDROID_ID or identifierForVendor."
                    ) {
                        Rows = {
                            Fact(title: "Device ID", value: p.DeviceId),
                        },
                    },
                    new AdwPreferencesGroup(title: "Application") {
                        Rows = {
                            Fact(title: "App Name", value: p.AppName),
                            Fact(title: "Package ID", value: p.PackageId),
                            Fact(title: "App Version", value: p.AppVersion),
                            Fact(title: "Build Number", value: p.BuildNumber),
                        },
                    },
                },
            }
        ) { TopBars = { new AdwHeaderBar { Title = "Device Info" } } };
    }

    private static AdwActionRow Fact(string title, string value)
    {
        return new AdwActionRow(title) {
            Suffixes = { new Text(value.Length == 0 ? "—" : value) },
        };
    }
}
