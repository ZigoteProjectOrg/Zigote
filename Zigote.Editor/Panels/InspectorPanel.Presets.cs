using Zigote.Editor.History;
using Zigote.Editor.Scene;
using Zigote.Runtime.Scene;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

// Dropdown<T> must be referenced with a concrete type — alias for clarity:

namespace Zigote.Editor.Panels;

public sealed partial class InspectorPanel
{
    private static int NearestLightPreset(float kelvin)
    {
        int best = 0;
        float bd = float.MaxValue;
        for (int i = 0; i < LightPresetKelvin.Length; i++)
        {
            float d = MathF.Abs(LightPresetKelvin[i] - kelvin);
            if (d < bd)
            {
                bd = d;
                best = i;
            }
        }

        return best;
    }

    /// <summary>A grid of one-click material-finish preset buttons (Car Paint / Chrome / Glass / …).</summary>
    private Widget BuildPresetRow()
    {
        var presets = MaterialPresets.All;
        var col = new Column {
            CrossAxisAlignment = CrossAxisAlignment.Start,
            MainAxisSize = MainAxisSize.Min,
        };
        for (int r = 0; r < presets.Count; r += 3)
        {
            var row = new Row {
                MainAxisAlignment = MainAxisAlignment.Start,
                CrossAxisAlignment = CrossAxisAlignment.Center,
            };
            for (int i = r; i < Math.Min(val1: r + 3, val2: presets.Count); i++)
            {
                var p = presets[i];
                row.Children.Add(
                    new SizedBox(
                        width: 74f,
                        child: new AdwButton(
                            label: p.Name,
                            onPressed: () => ApplyMaterialPreset(p)
                        ) {
                            Compact = true,
                        }
                    )
                );
                row.Children.Add(new SizedBox(4f));
            }

            col.Children.Add(row);
            col.Children.Add(new SizedBox(height: 4f));
        }

        return col;
    }

    /// <summary>
    ///     Apply a finish preset to the selected mesh (and, when toggled, all its mesh descendants)
    ///     as one undo step.
    /// </summary>
    private void ApplyMaterialPreset(MaterialPreset preset)
    {
        if (_shown is null) return;
        var root = _shown;
        var scope = _applyToSubMeshes
            ? root.Descendants().Prepend(root)
            : new[] { root }.AsEnumerable();
        var targets = scope.Where(n => n.Kind == NodeKind.Mesh).ToList();
        if (targets.Count == 0) return;

        var before = targets.Select(MeshMaterialSnapshot.Of).ToList();
        _state.History.Execute(
            new CompositeCommand(
                state: _state,
                apply: () =>
                {
                    foreach (var t in targets) preset.ApplyTo(t);
                },
                revert: () =>
                {
                    for (int i = 0; i < targets.Count; i++) before[i].RestoreTo(targets[i]);
                }
            )
        );
        Rebuild();
    }
}
