using Xunit;
using Zigote.World;

namespace Zigote.Tests;

public class TagIndexTests
{
    [Fact]
    public void Set_And_WithTag_RoundTrip()
    {
        var tags = new TagIndex();
        tags.Set(1, "Enemy");
        tags.Set(2, "Enemy");
        tags.Set(3, "Pickup");

        var results = new List<int>();
        Assert.Equal(2, tags.WithTag("Enemy", results));
        Assert.Contains(1, results);
        Assert.Contains(2, results);
        Assert.Equal(1, tags.Count("Pickup"));
        Assert.Equal(0, tags.Count("Boss"));
    }

    [Fact]
    public void Set_Retag_MovesBetweenLists()
    {
        var tags = new TagIndex();
        tags.Set(1, "Enemy");
        tags.Set(1, "Corpse");

        Assert.Equal(0, tags.Count("Enemy"));
        Assert.Equal(1, tags.Count("Corpse"));
        Assert.Equal("Corpse", tags.TagOf(1));
    }

    [Fact]
    public void Set_NullOrEmpty_Untags()
    {
        var tags = new TagIndex();
        tags.Set(1, "Enemy");
        tags.Set(1, null);
        Assert.Equal(0, tags.Count("Enemy"));
        Assert.Null(tags.TagOf(1));

        tags.Set(2, "Enemy");
        tags.Set(2, "");
        Assert.Equal(0, tags.Count("Enemy"));
    }

    [Fact]
    public void Set_SameTagTwice_DoesNotDuplicate()
    {
        var tags = new TagIndex();
        tags.Set(1, "Enemy");
        tags.Set(1, "Enemy");

        var results = new List<int>();
        Assert.Equal(1, tags.WithTag("Enemy", results));
    }

    [Fact]
    public void Remove_DropsTheId()
    {
        var tags = new TagIndex();
        tags.Set(1, "Enemy");
        tags.Remove(1);
        Assert.Equal(0, tags.Count("Enemy"));
        Assert.Null(tags.TagOf(1));
    }

    [Fact]
    public void WithTag_ClearsTheResultListFirst()
    {
        var tags = new TagIndex();
        tags.Set(1, "Enemy");

        var results = new List<int> { 42 };
        tags.WithTag("Enemy", results);
        Assert.Equal([1], results);
    }

    [Fact]
    public void Clear_EmptiesEverything()
    {
        var tags = new TagIndex();
        tags.Set(1, "Enemy");
        tags.Clear();
        Assert.Equal(0, tags.Count("Enemy"));
        Assert.Null(tags.TagOf(1));
    }
}
