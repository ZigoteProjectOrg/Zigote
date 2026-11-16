using Xunit;
using Zigote.Core;
using Zigote.UI.Adwaita;
using Zigote.UI.Widgets;

namespace Zigote.Tests;

public class AdwNumberInputTests
{
    private static Widget Find(Widget root, string typeName)
    {
        if (root.GetType().Name == typeName) return root;
        foreach (var child in root.GetChildren())
        {
            if (Find(root: child, typeName: typeName) is { } hit)
                return hit;
        }

        return null!;
    }

    [Fact]
    public void ScrubbingWithMoreDecimalsThanAFloatHolds_DoesNotThrow()
    {
        // Regression: the grip rounded with MathF.Round(x, digits) clamped to 15, the double
        // overload's limit; the float overload accepts 0..6 and threw for anything above.
        var input = new AdwNumberInput(value: 0f, step: 0.5f) { Decimals = 8 };
        float reported = float.NaN;
        input.OnChanged = v => reported = v;

        var grip = Find(root: input, typeName: "ScrubGrip");
        Assert.NotNull(grip);
        grip.OnPointerDown(new Offset(x: 0f, y: 0f));
        grip.OnPointerMove(new Offset(x: 10f, y: 0f));

        Assert.True(input.Value > 0f);
        Assert.Equal(expected: input.Value, actual: reported);
    }
}
