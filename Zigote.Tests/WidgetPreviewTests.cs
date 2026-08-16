using Xunit;
using Zigote.UI.Host;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     <see cref="WidgetPreview" /> resolution — the part an editor depends on. A wrong name has to
///     produce a widget rather than an exception: under <c>dotnet watch</c> a throw ends the session,
///     and the name is wrong precisely while it is being typed.
/// </summary>
public class WidgetPreviewTests
{
    [Fact]
    public void ResolvesAWidgetTypeByFullName() =>
        Assert.IsType<PreviewSample>(WidgetPreview.Resolve(typeof(PreviewSample).FullName!));

    [Fact]
    public void ResolvesAStaticFactoryMethod() => Assert.IsType<Center>(
        WidgetPreview.Resolve($"{typeof(PreviewSample).FullName}.Factory")
    );

    [Theory]
    [InlineData("Nope.NotAType")]
    [InlineData("Zigote.Tests.WidgetPreviewTests")] // exists, but is not a Widget
    [InlineData("Zigote.UI.Widgets.Widget")] // a Widget, but abstract
    public void ReportsFailuresAsAWidgetInsteadOfThrowing(string target) =>
        Assert.NotNull(WidgetPreview.Resolve(target));

    [Fact]
    public void AConstructorThatThrowsBecomesAMessage()
    {
        // Not the exception itself: the previewer stays up so the next save can fix it.
        Assert.NotNull(WidgetPreview.Resolve(typeof(PreviewExploding).FullName!));
    }

    [Fact]
    public void PropertiesComeFromDefaultedConstructorParameters()
    {
        var widget = Assert.IsType<PreviewParameterised>(
            WidgetPreview.Resolve($"{typeof(PreviewParameterised).FullName}?title=Espresso&count=3")
        );
        Assert.Equal(expected: "Espresso", actual: widget.Title);
        Assert.Equal(expected: 3, actual: widget.Count);
    }

    [Fact]
    public void AValueThatWillNotConvertFallsBackToTheDefault()
    {
        // Half-typed numbers are the normal state of a property being edited; a preview that fails
        // on them flashes an error between every keystroke.
        var widget = Assert.IsType<PreviewParameterised>(
            WidgetPreview.Resolve($"{typeof(PreviewParameterised).FullName}?count=notanumber")
        );
        Assert.Equal(expected: 1, actual: widget.Count);
    }

    [Fact]
    public void ValuesAreUrlDecodedSoTheyMaySpellAnything()
    {
        var widget = Assert.IsType<PreviewParameterised>(
            WidgetPreview.Resolve($"{typeof(PreviewParameterised).FullName}?title=a%26b+c")
        );
        Assert.Equal(expected: "a&b c", actual: widget.Title);
    }

    [Fact]
    public void FactoryMethodsTakeTheirPropertiesToo()
    {
        Assert.IsType<Center>(
            WidgetPreview.Resolve($"{typeof(PreviewParameterised).FullName}.Made?label=hi")
        );
    }

    [Fact]
    public void DescriptorsReportTheAnnotationAndTheKnobs()
    {
        var described = InTestAssembly(() =>
            WidgetPreview.Descriptors()
                .Single(d => d.Target == typeof(PreviewParameterised).FullName)
        );

        Assert.True(described.Annotated);
        Assert.Equal(expected: "Sample card", actual: described.Label);
        Assert.Equal(expected: 412, actual: described.Width);
        Assert.Equal(expected: "dark", actual: described.Theme);

        Assert.Collection(
            collection: described.Parameters,
            p => Assert.Equal(expected: ("title", "string", "Card"), actual: (p.Name, p.Kind, p.Value)),
            p => Assert.Equal(expected: ("count", "int", "1"), actual: (p.Name, p.Kind, p.Value)),
            p =>
            {
                Assert.Equal(expected: ("align", "enum"), actual: (p.Name, p.Kind));
                Assert.Contains(expected: "End", collection: p.Options);
            }
        );
    }

    [Fact]
    public void AnnotatedTargetsComeFirst()
    {
        // The point of the attribute in an app with two hundred widget types: the handful someone
        // meant to be looked at are the top of the list, not scattered through it alphabetically.
        var targets = InTestAssembly(() => WidgetPreview.Candidates().ToList());
        Assert.True(
            targets.IndexOf(typeof(PreviewParameterised).FullName!) <
            targets.IndexOf(typeof(PreviewSample).FullName!)
        );
    }

    [Fact]
    public void AnUnannotatedTypeIsStillBuiltTheWayItAlwaysWas()
    {
        // The knobs are opt-in for a reason: `Foo()` and `Foo(int n = 0)` are two constructors, and
        // only the first was ever previewed before. Taking the richer one would silently change what
        // an existing preview shows.
        var widget = Assert.IsType<PreviewTwoWays>(
            WidgetPreview.Resolve(typeof(PreviewTwoWays).FullName!)
        );
        Assert.Equal(expected: "no-args", actual: widget.Marker);
    }

    [Fact]
    public void SplitSeparatesTheTargetFromItsValues()
    {
        (string target, var values) = WidgetPreview.Split("My.App.Card?title=Hi&sale=true");
        Assert.Equal(expected: "My.App.Card", actual: target);
        Assert.Equal(expected: "Hi", actual: values["title"]);
        Assert.Equal(expected: "true", actual: values["sale"]);
        Assert.Empty(WidgetPreview.Split("My.App.Card").Values);
    }

    [Fact]
    public void CandidatesListsTheAssemblyItIsPointedAt()
    {
        Environment.SetEnvironmentVariable(
            variable: "ZIGOTE_PREVIEW_ASSEMBLY",
            value: "Zigote.Tests"
        );
        try
        {
            Assert.Contains(
                expected: typeof(PreviewSample).FullName,
                collection: WidgetPreview.Candidates()
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable: "ZIGOTE_PREVIEW_ASSEMBLY", value: null);
        }
    }

    /// <summary>Discovery reads the entry assembly, which under a test runner is not this one.</summary>
    private static T InTestAssembly<T>(Func<T> work)
    {
        Environment.SetEnvironmentVariable(
            variable: "ZIGOTE_PREVIEW_ASSEMBLY",
            value: "Zigote.Tests"
        );
        try
        {
            return work();
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable: "ZIGOTE_PREVIEW_ASSEMBLY", value: null);
        }
    }
}

internal sealed class PreviewSample : Center
{
    public static Widget Factory() => new Center();
}

internal enum PreviewAlign { Start, End }

internal sealed class PreviewTwoWays : Center
{
    public PreviewTwoWays() => Marker = "no-args";

    public PreviewTwoWays(string marker = "with-args") => Marker = marker;

    public string Marker { get; }
}

[Preview("Sample card", Width = 412, Height = 915, Theme = "dark")]
internal sealed class PreviewParameterised(
    string title = "Card",
    int count = 1,
    PreviewAlign align = PreviewAlign.Start
) : Center
{
    public string Title { get; } = title;
    public int Count { get; } = count;
    public PreviewAlign Align { get; } = align;

    public static Widget Made(string label = "made") => new Center(child: new Text(label));
}

internal sealed class PreviewExploding : Center
{
    public PreviewExploding() => throw new InvalidOperationException("boom");
}
