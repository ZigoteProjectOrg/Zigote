using Xunit;
using Zigote.Core;
using Zigote.UI.Adwaita;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;

namespace Zigote.Tests;

/// <summary>
///     <see cref="AdwSuggestionEntry" /> reacts to text changes by pushing a completion overlay onto
///     the owning window — so every text change that happens with no window (construction, an
///     external write, a detached widget) has to be a no-op rather than a null deref. These drive
///     it unmounted, which is exactly the state the constructor runs in.
/// </summary>
public class AdwSuggestionEntryTests
{
    private static AdwSuggestionEntry Entry(Action<string>? onCommit = null, int suggestions = 3)
    {
        return new AdwSuggestionEntry(
            "initial",
            _ => Enumerable.Range(0, suggestions).Select(i => ($"value{i}", $"display{i}")).ToList(),
            onCommit ?? (_ => { })
        );
    }

    [Fact]
    public void ConstructingWithATextValueDoesNotTouchAnOverlay()
    {
        var entry = Entry();
        Assert.Equal("initial", entry.Text);
    }

    [Fact]
    public void WritingTextWhileUnmountedIsANoOp()
    {
        var entry = Entry();
        entry.Text = "changed";
        Assert.Equal("changed", entry.Text);

        // Detach is the teardown path; it hides a popup that was never shown.
        entry.Detach();
    }

    [Fact]
    public void SubmittingCommitsTheTypedValueEvenWhenNothingWasSuggested()
    {
        string? committed = null;
        var entry = Entry(v => committed = v, 0);
        entry.Text = "hand/typed/path.png";

        // What Enter reaches: the entry's own submit handler.
        entry.OnSubmitted!("hand/typed/path.png");

        Assert.Equal("hand/typed/path.png", committed);
    }

    [Fact]
    public void ItMeasuresAsAPlainEntryOfTheSameDensity()
    {
        var entry = Entry();
        entry.Compact = true;
        var wrapper = new ThemeProvider(ThemeData.Dark, entry);
        wrapper.Measure(new Constraints(0f, 240f, 0f, 200f));
        wrapper.Layout(new Offset(0f, 0f));

        Assert.Equal(AdwMetrics.CompactControlHeight, entry.Bounds.Height, 3);
    }
}
