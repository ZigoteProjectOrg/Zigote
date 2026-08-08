# Zigote.UI.Charts

A composable, declarative charting library for `Zigote.UI`. A `Chart` holds an ordered list of **marks** that share two
auto-inferred (or pinned) **scales** and compose freely — a `BarMark`, a
`LineMark`, and a `RuleMark` overlay in one plot with no special-casing. It references only `Zigote.UI`

+ `Zigote.Core`; the scale/mark math is pure logic and headless — the whole library is unit-tested by building a tree,
  laying it out, painting into a `PaintList`, and asserting the emitted commands (~54 tests).

## The model

1. **Marks** are the visual content — `BarMark`, `LineMark`, `AreaMark`, `PointMark`, `RuleMark`,
   `RectangleMark`, `SectorMark`. Add as many as you like to one chart; they layer in order.
2. **Scales** map data → screen. The axis infers its scale kind (linear / category / time / log) from the first
   `ChartValue` it sees, or you pin one (`XScale`/`YScale`).
3. The **`Chart`** widget wires it together: auto axes, grid, legend, hover, animation, scroll, and zoom. It's retained
   like every Zigote widget — build it once, mutate the data, call `InvalidateData`.

## Quick start

```csharp
record Sale(string Month, double Amount, string Region);

var data = new List<Sale>
{
    new("Jan", 120, "West"), new("Feb", 180, "West"), new("Mar", 140, "West"),
    new("Jan",  90, "East"), new("Feb", 130, "East"), new("Mar", 210, "East"),
};

var chart = new Chart
{
    Theme = ThemeData.Dark,
    Marks =
    {
        // series: d => d.Region  -> stacked bars, auto-coloured + legend
        BarMark.Of(data, d => d.Month, d => d.Amount, series: d => d.Region),
    },
};
```

`ChartValue` — the plottable unit — is implicitly convertible from `double`/`int`/`string`/`DateTime`, so selectors read
naturally (`x: d => d.Month`, `y: d => d.Amount`). Every mark has an `Of<T>(...)`
factory that infers `T`.

## Marks

| Mark               | Does             | Notable options                                                                                                             |
|--------------------|------------------|-----------------------------------------------------------------------------------------------------------------------------|
| `BarMark`          | Bars             | `Stacking` (Standard/None = grouped/diverging/**Normalized** = 100%/**Center** = streamgraph), horizontal, rounded free-end |
| `LineMark`         | Lines            | `Interpolation` (Linear/Monotone/Step), dashes, `ShowSymbols`, `MaxRenderPoints` (LTTB decimation)                          |
| `AreaMark`         | Filled areas     | stacked (same four modes); `UsePolygonFill` for seam-free translucent fills                                                 |
| `PointMark`        | Scatter / bubble | `SizeBy` (bubbles), circle/square/ring/triangle/diamond symbols                                                             |
| `RuleMark`         | Threshold lines  | horizontal or vertical reference rules                                                                                      |
| `RectangleMark`    | Heatmaps         | `FillBy` colour ramp                                                                                                        |
| `SectorMark`       | Pie / donut      | polygon-filled wedges, polar hit-testing                                                                                    |
| `FunctionLineMark` | `y = f(x)`       | sampled per pixel across the visible window (tracks pan/zoom); NaN/∞ (poles) break the stroke                               |

Data marks also have **vectorized factories** over plain arrays — `LineMark.Of(ys)`,
`LineMark.Of(xs, ys)` (same for `AreaMark`/`PointMark`) — no row type or selectors needed.

```csharp
new Chart
{
    Marks =
    {
        BarMark.Of(data, d => d.Month, d => d.Amount),
        LineMark.Of(trend, d => d.Month, d => d.Value),   // overlays the bars
        new RuleMark { Y = 150 },                          // target threshold
    },
};
```

## Scales & axes

Scales live in `Scales/` and are pure logic: `LinearScale` (nice domains, `IncludeZero`, pinned
`Min`/`Max`), `LogScale` (decade ticks), `BandScale` (categories → equal bands), `TimeScale`
(calendar-aware tick units + labels). All support **windowing** (`SetVisibleWindow`/`FullExtent`) — that's what backs
scroll and zoom. Pin one explicitly when you don't want inference:

```csharp
new Chart
{
    YScale = new LinearScale { Min = 0, Max = 250 },
    XAxis  = { ShowGrid = true },
    Marks  = { /* … */ },
};
```

Axes customize per tick (the AxisMarks analogue): `TickValues` pins explicit tick positions,
`Formatter` labels them, and `TickStyle` styles each tick's grid line + label from its domain value (a
struct-in/struct-out callback — safe on the paint hot path):

```csharp
YAxis = {
    TickValues = [0.0, 100.0, 200.0, 300.0],
    Formatter  = v => $"{v.Numeric:F0} ms",
    TickStyle  = v => v.Numeric == 300
        ? new AxisTickStyle { GridColor = red, GridWidth = 2f, LabelColor = red }
        : default,
},
```

## Interaction

- **Hover** — lollipop + tooltip + crosshair by default (`ShowTooltip`, `OnHoverChanged`,
  `OnPointTap`).
- **Scroll** — `ScrollableX`/`ScrollableY` + `VisibleXDomainLength`/`VisibleYDomainLength` show a window; drag or
  two-finger scroll to pan, with stick-to-newest and a position indicator.
- **Zoom** — `ZoomableX`/`ZoomableY`: ⌘/Ctrl-scroll zooms around the cursor, plus `ZoomBy(factor,
  focus)` / `ResetZoom()`. `MinVisibleFraction` bounds the zoom-in.
- **Range selection** — `EnableXSelection` + `OnXRangeSelected` (an x-range-selection API): drag paints a band and
  reports the x-domain interval; a click clears it.
- **Dual y-axes** — a mark's `UseSecondaryYAxis` binds it to a second, opposite-side y-scale (`YScale2`/`YAxis2`); the
  two contexts share series colours + the hover registry (e.g. price + volume).

```csharp
var chart = new Chart
{
    ScrollableX          = true,
    VisibleXDomainLength = 30,     // show a 30-wide window
    ZoomableX            = true,
    Marks                = { LineMark.Of(series, d => d.Time, d => d.Price) },
};
chart.ScrollToEnd();
```

## Updating data

The `Chart` is retained — mutate your marks' data and tell it to refresh:

```csharp
data.Add(new("Apr", 200, "West"));
chart.InvalidateData(animate: true);   // marks morph from their previous values
```

`AnimateIn()` plays the entrance animation (grow / reveal / sweep); host code ticks running animations via
`AdvanceAnimation(dt)`.

## Annotations

`Chart.Annotations` holds `ChartAnnotation`s (text or dot) pinned to a data coordinate; each is re-projected every frame
so it tracks scroll, zoom, and morph.

## Custom overlays — `ChartProxy` + `OverlayPainter`

`chart.Proxy` (valid after layout) converts both ways between data and screen space over the finalized, windowed scales:
`PositionX`/`PositionY`/`Position` and the inverses
`XValueAt`/`YValueAt` (numeric value, seconds for time, band index for categories; secondary-axis overloads included).
`chart.OverlayPainter = (paint, proxy) => …` paints above the marks, clipped to the plot, every frame — the
`chartOverlay` analogue. Both are allocation-free by construction (`ChartProxy` is a readonly struct); keep the callback
geometry-only and route per-frame text through `CachedText`.

## Debug panels (dogfood)

The library powers its own diagnostics: a host that calls `ChartsDebugMenu.Install()` after `App`
construction swaps the built-in Overview + Profiler debug panels for charting versions (rolling FPS/CPU/memory, frame
breakdown, hottest scopes) and adds **Memory**, **Pipeline** (3D·Render), and **UI Paint** (2D·UI). `DebugChartHost`
bridges a retained `Chart` into the immediate-mode debug menu. Wired in the editor, player, and gallery hosts.

## Renderer note

The one native dependency is **`CMD_POLYGON`** — a filled convex/simple polygon (triangle-fanned by the shape pipeline).
It backs the filled triangle/diamond symbols, sector wedges, and the opt-in seam-free area fill. Everything else is pure
paint-primitive composition over `Zigote.UI`.

## Known v1 limits

- **No pinch zoom** — the engine surfaces no magnify gesture, so zoom is ⌘/Ctrl-wheel or the
  `ZoomBy`/`ResetZoom` API.
- **No rotated axis labels** — there is no text-transform paint primitive.
- **No y-axis range selection** — selection is x-only.
- Area fills default to the zero-alloc rect-strip path; `UsePolygonFill` opts into polygons at a small per-segment
  allocation cost.
