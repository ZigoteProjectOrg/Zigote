namespace Zigote.Graphs.Core;

/// <summary>
///     Editor-only layout and annotation data. Stripped from runtime builds.
///     Lives in the graph document but is not semantically meaningful.
/// </summary>
public sealed class GraphEditorData
{
    public Dictionary<Guid, NodeLayoutData> NodeLayouts { get; } = new();
    public List<GraphComment> Comments { get; } = [];
    public List<GraphGroup> Groups { get; } = [];

    public float ViewOffsetX { get; set; }
    public float ViewOffsetY { get; set; }
    public float Zoom { get; set; } = 1.0f;
}

public sealed class NodeLayoutData
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public bool IsCollapsed { get; set; }
    public Guid? GroupId { get; set; }
}

public sealed class GraphComment
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
}

public sealed class GraphGroup
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public List<Guid> NodeIds { get; } = [];
}
