using System.Reflection;
using System.Runtime.CompilerServices;
using Zigote.Core;
using Zigote.UI.Host;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.Localizations.Tests;

/// <summary>
///     Translations read inside a Watch resolve on its first build and on out-of-walk rebuilds, and
///     <c>context.SetLocale</c> works from a click handler.
/// </summary>
public class WatchLookupTests
{
    private sealed class Page : ComposedWidget
    {
        public readonly Zigote.Core.State.Signal<int> Tick = new(0);
        public string? Outside, Inside;
        public BuildContext? Ctx;

        protected override Widget Build(BuildContext context)
        {
            Ctx = context;
            Outside = context.Tr("hi");
            return new Column(children: [
                new Watch(() =>
                {
                    _ = Tick.Value;
                    Inside = context.Tr("hi");
                    return new SizedBox(width: 10, height: 10);
                }),
            ]);
        }
    }

    [Fact]
    public void Tr_inside_watch_resolves()
    {
        var page = new Page();
        var scope = new LocalizationsScope {
            Bundle = new LocalizationBundle(
                new LocalizationCatalog(Locale.En) { ["hi"] = "Hello" },
                new LocalizationCatalog(Locale.Es) { ["hi"] = "Hola" }
            ),
            SupportedLocales = { Locale.En, Locale.Es },
            InitialLocale = Locale.En,
            UseSystemLocale = false,
            Child = page,
        };
        scope.Attach(owner: FakeApp(), parent: null);
        var box = new Constraints(minWidth: 0, maxWidth: 400, minHeight: 0, maxHeight: 400);
        scope.Measure(box);
        Assert.Equal(expected: "Hello", actual: page.Outside);
        Assert.Equal(expected: "Hello", actual: page.Inside);

        // A signal write outside the walk (a click handler) rebuilds the Watch out of the walk.
        page.Inside = null;
        page.Tick.Value++;
        scope.Measure(box);
        Assert.Equal(expected: "Hello", actual: page.Inside);

        // SetLocale from a click handler — outside the walk, where the context has no live scope.
        Assert.True(page.Ctx!.SetLocale(Locale.Es));
        scope.Measure(box);
        Assert.Equal(expected: "Hola", actual: page.Outside);
        Assert.Equal(expected: "Hola", actual: page.Inside);
    }

    private static App FakeApp()
    {
        var app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        typeof(App).GetField(name: "_repaint", bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(obj: app, value: Activator.CreateInstance(typeof(App).GetField("_repaint", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType));
        var overlays = typeof(App).GetField(name: "_overlays", bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic)!;
        overlays.SetValue(obj: app, value: Activator.CreateInstance(overlays.FieldType));
        return app;
    }
}
