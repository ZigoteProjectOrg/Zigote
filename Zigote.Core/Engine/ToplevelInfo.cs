namespace Zigote.Core.Engine;

/// <summary>State bits of a compositor toplevel (wlr-foreign-toplevel-management).</summary>
[Flags]
public enum ToplevelState : uint
{
    None = 0,
    Maximized = 1,
    Minimized = 2,
    Activated = 4,
    Fullscreen = 8
}

/// <summary>What a shell may ask the compositor to do with a toplevel.</summary>
public enum ToplevelAction : uint
{
    Activate = 0,
    Close = 1,
    Minimize = 2,
    Unminimize = 3,
    Maximize = 4,
    Unmaximize = 5
}

/// <summary>One window as the compositor describes it to a shell. <see cref="Id" /> is stable for its lifetime.</summary>
public readonly record struct ToplevelInfo(ulong Id, string AppId, string Title, ToplevelState State);

/// <summary>A connected output, as the compositor advertises it; logical size (after scale).</summary>
public readonly record struct OutputInfo(ulong Id, string Name, uint Width, uint Height, float Scale);

/// <summary>State bits of an ext-workspace-v1 workspace.</summary>
[Flags]
public enum WorkspaceState : uint
{
    None = 0,
    Active = 1,
    Urgent = 2,
    Hidden = 4
}

/// <summary>A workspace, as the compositor advertises it.</summary>
public readonly record struct WorkspaceInfo(ulong Id, string Name, WorkspaceState State, bool CanActivate);
