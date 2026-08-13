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
                PropRow.StatusLine(
                    text: "◆ Prefab instance (template missing)",
                    color: _theme.Hint,
                    theme: _theme
                )
            );
            _rows.Add(PropRow.Spacer(6f));
            return;
        }

        var overridden = PrefabOverrides.ApplicableTo(node)
            .Where(c => PrefabOverrides.IsOverridden(
                    component: c,
                    instance: node,
                    template: doc.Template
                )
            )
            .ToList();

        _rows.Add(
            PropRow.StatusLine(
                text: overridden.Count == 0
                    ? $"◆ Prefab · {doc.Name}"
                    : $"◆ Prefab · {doc.Name}  ({overridden.Count} overridden)",
                color: _theme.Accent,
                theme: _theme
            )
        );

        foreach (var c in overridden)
        {
            var component = c;
            _rows.Add(
                PropRow.ActionButton(
                    label: $"Revert {component}",
                    onClick: () => RevertPrefabComponent(node: node, component: component)
                )
            );
        }

        if (overridden.Count > 1)
        {
            _rows.Add(
                PropRow.ActionButton(
                    label: "Revert All",
                    onClick: () =>
                    {
                        foreach (var c in overridden)
                            RevertPrefabComponent(node: node, component: c);
                    }
                )
            );
        }

        _rows.Add(PropRow.Spacer(6f));
    }

    private void RevertPrefabComponent(SceneNode node, PrefabComponent component)
    {
        if (_prefabDoc is not { } doc) return;
        object before = PrefabOverrides.Capture(component: component, node: node);
        _state.History.Execute(
            new CompositeCommand(
                state: _state,
                apply: () => PrefabOverrides.Revert(
                    component: component,
                    instance: node,
                    template: doc.Template
                ),
                revert: () => PrefabOverrides.Restore(
                    component: component,
                    node: node,
                    snapshot: before
                )
            )
        );
        Rebuild(); // refresh the override indicators
    }
}
