# Graph editor controls

`GraphEditorPanel.InspectorVisible` hides the complete inspector, optional header, and divider while retaining the
user's split ratio. Hosts without the built-in toolbar can set this property themselves, or pass
`inspectorVisible: false` to the constructor.

Required inputs (`PinDefinition.IsRequired`) show a red node status dot/border and a “required” pin label when they have
neither a declared non-null default nor a connection from an existing output pin. The editor merges these checks with
domain validation and compilation diagnostics. Domain validation remains responsible for type compatibility and
graph-specific rules. Checks also refresh after undo/redo and when automatic compilation is disabled.

Inline property controls:

- Click a numeric value to type; the current value is selected for replacement. The label and unit stay visible, and
  text scales with the node. Enter commits and Escape cancels. Invalid input shows an error border and a hint; losing
  focus discards invalid input. Finite values are clamped to the declared range.
- Drag numeric rows to scrub. When both `Min` and `Max` are provided, dragging maps to the range track. Each drag
  creates one undo command; pointer cancellation restores the original override.
- Click boolean switches to toggle. Moving away or releasing outside cancels the toggle.
- Click integer enum controls to cycle through `EnumLabels`; the inspector provides the full dropdown.
- Click plain string properties to edit text in place. Specialized editors such as gradients remain in the inspector.

Use property metadata to configure controls for audio and game logic:

```csharp
new PropertyDefinition {
    Id = "gain",
    DisplayName = "Gain",
    Type = GraphTypeRef.Float,
    DefaultValue = GraphValue.FromFloat(0f),
    Min = -60f,
    Max = 12f,
    Step = 0.1f,
    Unit = "dB",
};
```

`Step` controls drag increments (defaults: 0.01 for floats, 1 for integers). Direct text entry allows precise values
between steps. These controls edit node properties; pin defaults remain definition-owned and domains decide how
properties affect their inputs and runtime behavior.

## Adding nodes

Right-click empty canvas, use **Add node** in the toolbar, or press **Shift+A / Space** to open the same searchable
picker. It uses the shared menu surface, elevation, selection colors, and typography. Right-clicking a node or cable
still opens its context actions; right-drag pans.

Type to filter, use **↑/↓** or **Page Up/Page Down** to navigate, then **Enter** to add. Scrolling keeps keyboard
selection visible. **Escape** or an outside click closes the picker. Dropping a cable onto empty space opens this picker
filtered to compatible connections.

## Selecting

- Click a node to select it. **Shift-click** or **Ctrl/Cmd-click** adds it to the selection, or removes it if it is
  already selected.
- Drag on empty canvas to select every card the rectangle touches (the whole card counts, not only its header). Hold
  Shift or Ctrl/Cmd to add to what is already selected.
- Dragging one node of a group moves the group. A plain click on one of them, with no drag, narrows the selection to
  that node.
- **Ctrl/Cmd A** selects all, **Esc** deselects (after first cancelling any gesture in progress), **arrows** nudge by
  one grid cell (Shift: one unit), **Ctrl/Cmd D** duplicates the selection together with the cables between its nodes,
  **Del** deletes it, **H** collapses or expands it, **F** frames it (or the whole graph when nothing is selected). Each
  is one undo step.
- Right-clicking a selected node acts on the whole selection (duplicate, collapse, disconnect cables, delete);
  right-clicking a node outside the selection selects just that one first.
- **Ctrl/Cmd C / X / V** copy, cut and paste the selected nodes with the cables between them. Paste lands at the pointer
  when it is over the canvas, otherwise beside the originals. The clipboard is in-process: it works across graphs of the
  same domain in one app, not between apps. Node types the target graph does not know are skipped.
- Modifier state for pointer gestures is read from the app at press time, so Shift held before the canvas had focus
  counts.
- The selection never keeps a node or cable that undo, redo or a delete has removed, and the last node added is the
  primary one the inspector edits.

## Cables

- Drag from a socket to connect; drop on empty canvas to add a connected node.
- Drag from an input that holds exactly one cable to pick that cable up. Drop it on another input to re-plug it (one
  undo step), back on its own input to leave it, or on empty canvas to unplug it.
- **Alt-click** a cable cuts it. Clicking selects it; **Shift-click** or **Ctrl/Cmd-click** selects several; a marquee
  that touches no card selects the cables it crosses. **Del** disconnects every selected cable in one undo step. Nodes
  and cables are never selected together.
- Drag a node that has no cables onto a cable to insert it: the cable turns green, and releasing re-wires it through the
  node's first compatible input and output. The move and the re-wiring are one undo step. A node that already has cables
  is only moved.
- Cables are drawn with a dark under-stroke so they stay readable where they cross each other or pass a card.

## Inspector

The right panel follows the selection:

- **One node:** title, a category chip in the card's colour, description, then one row per property with the label (and
  unit) on the left and the control on the right. A reset button on each row lights up when the value differs from the
  definition's default. Properties marked `InspectorOnly` are grouped under **Setup**; diagnostics for the node appear
  under **Problems**.
- **Several nodes:** a count, the node names grouped, and Collapse/Expand and Delete for the whole selection.
- **A cable:** its two ends and a Disconnect button. **Several cables:** a count, their routes, and Disconnect all.
- **Nothing:** the graph's name and counts, the build result, and a shortcut list.

Edits made in the inspector no longer rebuild the panel under the control being used, so a text field keeps focus while
you type.

## Navigating

- A mouse wheel zooms toward the pointer. Two fingers on a trackpad pan. **Ctrl/Cmd + scroll** zooms on either, and
  right-drag pans.
- The native layer flags each scroll as precise (trackpad) or not. On macOS that comes from the system; elsewhere it
  means the delta was fractional, so the canvas keeps panning for 400 ms after a precise event.
- On a touch screen, two fingers pan and pinch to zoom.
