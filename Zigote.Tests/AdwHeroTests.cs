using Xunit;
using Zigote.Core;
using Zigote.UI.Adwaita;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>The part of <see cref="AdwHero" /> that is plain widget behaviour: an unpaired hero is its child.</summary>
public class AdwHeroTests
{
    [Fact]
    public void An_unpaired_hero_measures_and_hits_as_its_child()
    {
        var child = new SizedBox(width: 40f, height: 30f);
        var hero = new AdwHero(tag: new object(), child: child) { Radius = 8f };

        var size = hero.Measure(Constraints.Loose(width: 200f, height: 200f));
        hero.Layout(new Offset(x: 10f, y: 20f));

        Assert.Equal(expected: 40f, actual: size.Width);
        Assert.Equal(expected: 30f, actual: size.Height);
        Assert.Equal(expected: child.Bounds, actual: hero.Bounds);
        Assert.Null(hero.HitTest(new Offset(x: 500f, y: 500f)));
        hero.Return(); // nothing to return to: a no-op, not a throw
    }
}
