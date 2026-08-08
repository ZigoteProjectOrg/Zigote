# DevTools widget tree — design

Scope: the **Widget tree** and **Selected** sections of `Zigote.UI.DevTools/Panels/UiInspectorPanel.cs`,
plus the on-screen debug toggles that sit above them.

## What is wrong today

| Problem | Cause |
| --- | --- |
| Trees larger than 400 rows are truncated with "collapse or search" | `MaxTreeRows = 400`; every visible row is a materialised `Pressable`/`Label` subtree inside a `Column` in a `ScrollView` |
| Rebuild cost is O(visible rows), 4×/s | `RebuildTree` re-creates the whole row list every 250 ms |
| Depth is unreadable in layered layouts | indentation is empty space, clamped at depth 20 × 10 px = 200 px of a ~320 px panel; nothing marks *which* ancestor a row belongs to |
| Rows are loose | 18 px rows, 4 px + 10 px/level padding, icon-sized chevrons |
| "Selected" is thin | size / constraints / bounds + a flat reflected property dump; no depth, child count, position-in-parent, or live dirty/counters |
| Overflow overlay paints from launch | `DevToolsController.ShowOverflow = true` — an unrequested red-stripe overlay on every app that opens devtools |

## Design

### 1. Model / view split

`RebuildTree` stops producing widgets. It flattens the visible tree into a `List<TreeNode>`
(`Widget, Depth, HasKids, Open`) — a struct list, no allocation per row beyond the list itself. A
cheap structural hash (count, node identities, selection) skips the flatten→publish step entirely
when nothing changed, so the 250 ms tick is usually free.

### 2. Virtualised rows

The flat list feeds `ListView.Builder` (already in `Zigote.UI/Widgets/Layout/ListView.cs`): only the
rows inside the viewport (+4 overscan) are built, measured, laid out and painted, and they are
dropped when they scroll out. Cost becomes O(viewport) instead of O(tree), so the row cap is
removed — a 20 000-node tree scrolls at the same price as a 20-node one.

Reveal-on-select needs to scroll to a row that may only exist after the list grew, so `ListView`
gains `EnsureVisible(index, margin)` — a deferred reveal applied in `Layout` once the scroll extent
is known, mirroring `ScrollView.EnsureVisible`.

### 3. Rainbow guidelines

Indentation becomes information. Each row draws one vertical 1 px rule per ancestor level, coloured
from a 12-hue depth palette (`DevKit.DepthColor`), so sibling rows at the same depth line up under a
rule of the same colour and a subtree is followable by eye through hundreds of rows. The guides are
one `LeafWidget` (`DevTreeGuides`) that paints the rules and reserves the indent width — no widget
per level.

Deeply layered trees clamp at `MaxGuideDepth = 12` levels (108 px); past that the guide strip prints
the real depth (`·17`) instead of growing, so the type name keeps its width.

### 4. Minimal paddings

Row height 18 → **15 px**, indent step 10 → **9 px**, row inset 4 → **2 px**, chevron 16 → **11 px**,
detail-text gap `Spacing.Xs` → 4 px. ~20 % more rows per screen at the same legibility. Touch is
unaffected: devtools rows were never finger targets, and the surrounding panel keeps `DevKit.Row`.

### 5. Selected info

Live rows (refreshed every frame, all through `CachedText`):

- **Size** — measured size (existing)
- **Constraints** — last constraints (existing)
- **Bounds** — screen rect (existing)
- **In parent** — offset relative to the parent's origin + the % of the parent it fills; the
  question the box diagram is opened to answer, in numbers
- **Tree** — `depth 7 · 3 children`
- **Dirty** — `B:0 L:0 P:1` build/layout/paint flags, live rather than frozen at selection time
- **Counts** — `M:12 L:12 P:340 R:3` measure/layout/paint/rebuild counters, live

Below them, the reflected property dump under a **Properties** header (unchanged, minus the rows
promoted above).

### 6. Overflow default

`ShowOverflow` defaults to `false`. It stays a toggle in the inspector and the quick-settings sheet;
opening devtools no longer repaints the app with overflow stripes.

## Not doing

- Horizontal scrolling of the tree — the depth clamp plus ellipsised labels cover layered trees; add
  it if names start getting cut in practice.
- Incremental/diffed flattening — the structural-hash skip already makes the steady state free.
- Multi-select, tree filtering by property, pinning — no demand yet.
