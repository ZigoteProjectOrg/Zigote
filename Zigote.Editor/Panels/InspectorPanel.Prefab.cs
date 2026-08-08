using Zigote.Editor.History;
using Zigote.Editor.Prefab;
using Zigote.Runtime.Scene;
// Dropdown<T> must be referenced with a concrete type — alias for clarity:

namespace Zigote.Editor.Panels;

public sealed partial class InspectorPanel
{
    // ── Prefab instance banner (override indicators + revert) ──────────────────

    /// <summary>
    ///     Header shown above a prefab instance's properties: the source prefab name, how many component
    ///     groups are overridden, and a per-component (+ "Revert All") revert button. Override state is a
    ///     diff of the instance's authorable POD components against the <c>.prefab</c> template — the same
    ///     per-component model as flecs <c>EcsPrefab</c>'s <c>Owns</c>.
    /// </summary>
    private void BuildPrefabBanner(SceneNode node)
    {
        if (node.PrefabSource != _prefabDocId)
        {
            _prefabDoc = _state.Prefabs.Load(node.PrefabSource);
            _prefabDocId = node.PrefabSource;
        }

        if (_prefabDoc is not { } doc)
        {
            _rows.Add(
                PropRow.StatusLine("◆ Prefab instance (template missing)", _theme.Hint, _theme)
            );
            _rows.Add(PropRow.Spacer(6f));
            return;
        }

        var overridden = PrefabOverrides.ApplicableTo(node)
            .Where(c => PrefabOverrides.IsOverridden(c, node, doc.Template))
            .ToList();

        _rows.Add(
            PropRow.StatusLine(
                overridden.Count == 0
                    ? $"◆ Prefab · {doc.Name}"
                    : $"◆ Prefab · {doc.Name}  ({overridden.Count} overridden)",
                _theme.Accent,
                _theme
            )
        );

        foreach (var c in overridden)
        {
            var component = c;
            _rows.Add(
                PropRow.ActionButton(
                    $"Revert {component}",
                    () => RevertPrefabComponent(node, component)
                )
            );
        }

        if (overridden.Count > 1)
            _rows.Add(
                PropRow.ActionButton(
                    "Revert All",
                    () =>
                    {
                        foreach (var c in overridden) RevertPrefabComponent(node, c);
                    }
                )
            );

        _rows.Add(PropRow.Spacer(6f));
    }

    private void RevertPrefabComponent(SceneNode node, PrefabComponent component)
    {
        if (_prefabDoc is not { } doc) return;
        var before = PrefabOverrides.Capture(component, node);
        _state.History.Execute(
            new CompositeCommand(
                _state,
                () => PrefabOverrides.Revert(component, node, doc.Template),
                () => PrefabOverrides.Restore(component, node, before)
            )
        );
        Rebuild(); // refresh the override indicators
    }
}