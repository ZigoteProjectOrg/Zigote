using Xunit;
using Zigote.UI.Host;
using Zigote.UI.Widgets;
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
    public void ResolvesAWidgetTypeByFullName()
    {
        Assert.IsType<PreviewSample>(WidgetPreview.Resolve(typeof(PreviewSample).FullName!));
    }

    [Fact]
    public void ResolvesAStaticFactoryMethod()
    {
        Assert.IsType<Center>(WidgetPreview.Resolve($"{typeof(PreviewSample).FullName}.Factory"));
    }

    [Theory]
    [InlineData("Nope.NotAType")]
    [InlineData("Zigote.Tests.WidgetPreviewTests")] // exists, but is not a Widget
    [InlineData("Zigote.UI.Widgets.Widget")] // a Widget, but abstract
    public void ReportsFailuresAsAWidgetInsteadOfThrowing(string target)
    {
        Assert.NotNull(WidgetPreview.Resolve(target));
    }

    [Fact]
    public void AConstructorThatThrowsBecomesAMessage()
    {
        // Not the exception itself: the previewer stays up so the next save can fix it.
        Assert.NotNull(WidgetPreview.Resolve(typeof(PreviewExploding).FullName!));
    }

    [Fact]
    public void CandidatesListsTheAssemblyItIsPointedAt()
    {
        Environment.SetEnvironmentVariable("ZIGOTE_PREVIEW_ASSEMBLY", "Zigote.Tests");
        try
        {
            Assert.Contains(typeof(PreviewSample).FullName, WidgetPreview.Candidates());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZIGOTE_PREVIEW_ASSEMBLY", null);
        }
    }
}

internal sealed class PreviewSample : Center
{
    public static Widget Factory()
    {
        return new Center();
    }
}

internal sealed class PreviewExploding : Center
{
    public PreviewExploding()
    {
        throw new InvalidOperationException("boom");
    }
}
