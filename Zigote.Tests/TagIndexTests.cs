using Xunit;
using Zigote.World;

namespace Zigote.Tests;

public class TagIndexTests
{
    [Fact]
    public void Set_And_WithTag_RoundTrip()
    {
        var tags = new TagIndex();
        tags.Set(id: 1, tag: "Enemy");
        tags.Set(id: 2, tag: "Enemy");
        tags.Set(id: 3, tag: "Pickup");

        var results = new List<int>();
        Assert.Equal(expected: 2, actual: tags.WithTag(tag: "Enemy", results: results));
        Assert.Contains(expected: 1, collection: results);
        Assert.Contains(expected: 2, collection: results);
        Assert.Equal(expected: 1, actual: tags.Count("Pickup"));
        Assert.Equal(expected: 0, actual: tags.Count("Boss"));
    }

    [Fact]
    public void Set_Retag_MovesBetweenLists()
    {
        var tags = new TagIndex();
        tags.Set(id: 1, tag: "Enemy");
        tags.Set(id: 1, tag: "Corpse");

        Assert.Equal(expected: 0, actual: tags.Count("Enemy"));
        Assert.Equal(expected: 1, actual: tags.Count("Corpse"));
        Assert.Equal(expected: "Corpse", actual: tags.TagOf(1));
    }

    [Fact]
    public void Set_NullOrEmpty_Untags()
    {
        var tags = new TagIndex();
        tags.Set(id: 1, tag: "Enemy");
        tags.Set(id: 1, tag: null);
        Assert.Equal(expected: 0, actual: tags.Count("Enemy"));
        Assert.Null(tags.TagOf(1));

        tags.Set(id: 2, tag: "Enemy");
        tags.Set(id: 2, tag: "");
        Assert.Equal(expected: 0, actual: tags.Count("Enemy"));
    }

    [Fact]
    public void Set_SameTagTwice_DoesNotDuplicate()
    {
        var tags = new TagIndex();
        tags.Set(id: 1, tag: "Enemy");
        tags.Set(id: 1, tag: "Enemy");

        var results = new List<int>();
        Assert.Equal(expected: 1, actual: tags.WithTag(tag: "Enemy", results: results));
    }

    [Fact]
    public void Remove_DropsTheId()
    {
        var tags = new TagIndex();
        tags.Set(id: 1, tag: "Enemy");
        tags.Remove(1);
        Assert.Equal(expected: 0, actual: tags.Count("Enemy"));
        Assert.Null(tags.TagOf(1));
    }

    [Fact]
    public void WithTag_ClearsTheResultListFirst()
    {
        var tags = new TagIndex();
        tags.Set(id: 1, tag: "Enemy");

        var results = new List<int> { 42 };
        tags.WithTag(tag: "Enemy", results: results);
        Assert.Equal(expected: [1], actual: results);
    }

    [Fact]
    public void Clear_EmptiesEverything()
    {
        var tags = new TagIndex();
        tags.Set(id: 1, tag: "Enemy");
        tags.Clear();
        Assert.Equal(expected: 0, actual: tags.Count("Enemy"));
        Assert.Null(tags.TagOf(1));
    }
}
