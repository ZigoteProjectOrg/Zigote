using Zigote.Core;
using Zigote.Core.State;
using Zigote.UI.Adwaita;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;
using Timer = System.Threading.Timer;

namespace Zigote.UI.Functional.Demo;

/// <summary>
///     The whole window, functions all the way down: every piece of UI here is a plain function
///     returning a <see cref="View" />. State is signals in closures, resources are
///     <see cref="View.OnMounted" />, styling comes from the ambient theme — no subclass anywhere
///     in this file.
/// </summary>
internal static class Views
{
    public static Widget Home(Func<bool> isDark, Action<bool> setDark)
    {
        // Stateful children are created once, in the closure. The window View below rebuilds on
        // every theme flip; these instances are re-adopted by the fresh tree, not reset — the
        // counter keeps counting, the clock keeps ticking.
        var counter = Counter();
        var clock = Clock();
        var basket = Basket();

        return new View(ctx =>
        {
            var palette = AdwPalette.For(ThemeProvider.Of(ctx));
            var page = new Column(crossAxisAlignment: CrossAxisAlignment.Stretch, spacing: 24f) {
                Children = {
                    Appearance(isDark: isDark, setDark: setDark),
                    counter,
                    clock,
                    basket,
                }
            };

            return new ColoredBox(
                color: palette.WindowBg,
                child: new AdwToolbarView(
                    new AdwClamp(
                        child: new Padding(
                            EdgeInsets.Symmetric(horizontal: 16f, vertical: 24f),
                            page
                        ),
                        maximumSize: 560f
                    )
                ) {
                    TopBars = { new AdwHeaderBar { Title = "Functional" } },
                }
            );
        });
    }

    /// <summary>
    ///     Inherited data: the switch renders the theme it sits under, and flipping it swaps the
    ///     app theme — which rebuilds every View on this page with the other palette.
    /// </summary>
    private static Widget Appearance(Func<bool> isDark, Action<bool> setDark) => new View(ctx =>
        new AdwPreferencesGroup(title: "Appearance") {
            Rows = {
                new AdwSwitchRow(
                    title: "Dark style",
                    subtitle: "Restyles every View on this page",
                    value: ThemeProvider.Of(ctx).IsDark,
                    onChanged: setDark
                ),
            },
        }
    );

    /// <summary>State in the closure: the signal outlives every rebuild of the View below it.</summary>
    private static Widget Counter()
    {
        var count = new Signal<int>(0);
        return new View(_ => new AdwPreferencesGroup(
            title: "State",
            description: "A signal in the closure; the row re-renders from it."
        ) {
            Rows = {
                new AdwActionRow(title: "Count", subtitle: $"{count.Value}") {
                    Suffixes = {
                        new AdwButton(label: "−1", onPressed: () => count.Value--) {
                            Style = AdwButtonStyle.Flat,
                        },
                        new AdwButton(label: "+1", onPressed: () => count.Value++) {
                            Style = AdwButtonStyle.Flat,
                        },
                    },
                },
            },
        });
    }

    /// <summary>Mount-scoped resource: starts on attach, stops on detach, writes off-thread.</summary>
    private static Widget Clock()
    {
        var now = new Signal<DateTime>(DateTime.Now);
        return new View(_ => new AdwPreferencesGroup(
            title: "Lifecycle",
            description: "A timer owned by the mount period, ticking on a background thread."
        ) {
            Rows = { new AdwActionRow(title: "Now", subtitle: $"{now.Value:HH:mm:ss}") },
        }) {
            OnMounted = () => new Timer(
                callback: _ => now.Value = DateTime.Now,
                state: null,
                dueTime: 0,
                period: 1000
            ),
        };
    }

    /// <summary>Tree shape from state: rows appear and disappear as the signal moves.</summary>
    private static Widget Basket()
    {
        string[] all = ["Apple", "Pear", "Plum", "Fig", "Quince"];
        var basket = new Signal<int>(2);
        return new View(_ =>
        {
            var group = new AdwPreferencesGroup(
                title: "Shape",
                description: "The row list itself comes from a signal."
            ) {
                HeaderSuffix = new Row(mainAxisSize: MainAxisSize.Min, spacing: 6f) {
                    Children = {
                        new AdwButton(
                            label: "Drop",
                            onPressed: () => basket.Value = Math.Max(1, basket.Value - 1)
                        ) { Style = AdwButtonStyle.Flat },
                        new AdwButton(
                            label: "Add",
                            onPressed: () => basket.Value = Math.Min(all.Length, basket.Value + 1)
                        ) { Style = AdwButtonStyle.Suggested },
                    },
                },
            };
            foreach (var fruit in all[..basket.Value])
                group.Rows.Add(new AdwActionRow(title: fruit));
            return group;
        });
    }
}
