namespace Zigote.Scripting;

/// <summary>Marks a property or field as editable in the inspector.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class ExportAttribute : Attribute
{
}

/// <summary>Overrides the display name shown in the inspector.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class EditorNameAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>Adds a min/max range slider hint in the inspector.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class EditorRangeAttribute(double min, double max) : Attribute
{
    public double Min { get; } = min;
    public double Max { get; } = max;
}

/// <summary>Tooltip shown on hover in the inspector.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class EditorTooltipAttribute(string tooltip) : Attribute
{
    public string Tooltip { get; } = tooltip;
}

/// <summary>Renders a Vec3 field as an RGB color picker in the inspector.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class EditorColorAttribute : Attribute
{
}