namespace AdwaitaGallery.Pages;

/// <summary>
///     Reactivity — the part that is Zigote rather than Adwaita. Nothing on this page calls a
///     refresh: signals hold the state, computed values derive from them, a Watch subscribes to
///     whatever it happened to read, and an effect runs on the side.
/// </summary>
public sealed class ReactivityPage : ComposedWidget
{
    private static readonly (string Name, float Price)[] Catalogue = [
        ("Espresso", 2.40f),
        ("Cortado", 3.10f),
        ("Filter", 3.60f),
    ];

    private readonly Signal<bool> _member = new(false);

    private readonly Signal<int>[] _quantities = [new(1), new(0), new(2)];

    private readonly Computed<float> _subtotal;
    private readonly Computed<float> _total;
    private Effect? _effect;

    // Bumped inside the computed — a plain field, not a signal: writing a signal from inside a
    // computation is how you get a cycle.
    private int _recomputes;

    public ReactivityPage()
    {
        // Derived, cached, and recomputed only when something it read actually changed.
        _subtotal = Computed.From(() =>
            {
                _recomputes++;
                float sum = 0f;
                for (int i = 0; i < Catalogue.Length; i++)
                    sum += _quantities[i].Value * Catalogue[i].Price;
                return sum;
            }
        );
        _total = Computed.From(() => _subtotal.Value * (_member.Value ? 0.9f : 1f));
    }

    public override void Attach(App owner, Widget? parent)
    {
        base.Attach(owner: owner, parent: parent);
        // An effect re-runs whenever its reads change — the seam for the work that is not UI
        // (persisting, logging, talking to a service).
        _effect ??= new Effect(() => _ = _total.Value);
    }

    public override void Detach()
    {
        base.Detach();
        _effect?.Dispose();
        _effect = null;
    }

    protected override Widget Build(BuildContext context)
    {
        var order = new AdwPreferencesGroup(
            title: "Order",
            description: "Each row writes one signal. Nothing here knows about the total."
        );
        for (int i = 0; i < Catalogue.Length; i++)
        {
            int index = i;
            (string name, float price) = Catalogue[i];
            order.Rows.Add(
                new AdwActionRow(title: name, subtitle: $"{price:0.00} each") {
                    Suffixes = {
                        new AdwSpinButton(
                            value: _quantities[index].Peek(),
                            min: 0,
                            max: 9,
                            step: 1,
                            onChanged: v => _quantities[index].Value = (int)v
                        ),
                    },
                }
            );
        }

        return new GalleryPage(
            title: "Reactivity",
            description:
            "Signals in, derived values out, and a widget tree that subscribes to exactly what it reads.",
            iconName: MaterialIcons.Bolt
        ) {
            ClampWidth = 680f,
            Children = {
                order,
                new AdwPreferencesGroup("Discount") {
                    Rows = {
                        new Watch(() => new AdwSwitchRow(
                                title: "Member",
                                subtitle: "Takes 10% off the subtotal",
                                value: _member.Value,
                                onChanged: v => _member.Value = v
                            )
                        ),
                    },
                },
                Demo.Titled(
                    title: "Derived",
                    description:
                    "A Computed caches: the counter only moves when a quantity actually changes.",
                    child: Demo.Stage(
                        new Column(
                            spacing: Spacing.Md,
                            mainAxisSize: MainAxisSize.Min,
                            crossAxisAlignment: CrossAxisAlignment.Center
                        ) {
                            Children = {
                                new Watch(() => Demo.Value($"subtotal = {_subtotal.Value:0.00}")),
                                new Watch(() => Demo.Value($"total    = {_total.Value:0.00}")),
                                new Watch(() =>
                                    {
                                        _ = _subtotal.Value; // re-read so this line refreshes too
                                        return Demo.Caption(
                                            $"subtotal recomputed {_recomputes} times"
                                        );
                                    }
                                ),
                            },
                        }
                    )
                ),
                Demo.Titled(
                    title: "The Same State, Three Ways",
                    description:
                    "One signal, three unrelated widgets — no shared parent, no callbacks between them.",
                    child: Demo.Stage(
                        new Column(
                            spacing: Spacing.Lg,
                            mainAxisSize: MainAxisSize.Min,
                            crossAxisAlignment: CrossAxisAlignment.Stretch
                        ) {
                            Children = {
                                new Watch(() => new AdwProgressBar(
                                        Math.Clamp(value: _total.Value / 20f, min: 0f, max: 1f)
                                    )
                                ),
                                new Watch(() => new AdwLevelBar(
                                        Math.Clamp(value: _total.Value / 20f, min: 0f, max: 1f)
                                    )
                                ),
                                new Watch(() => new Align(
                                        alignment: Alignment.Center,
                                        child: new AdwButton(
                                            _total.Value > 0f ? $"Pay {_total.Value:0.00}" : "Empty"
                                        ) {
                                            Style = AdwButtonStyle.Suggested,
                                            Pill = true,
                                            Enabled = _total.Value > 0f,
                                        }
                                    ) { HeightFactor = 1f }
                                ),
                            },
                        }
                    )
                ),
                Demo.Group(
                    title: "The Pieces",
                    description: null,
                    new AdwActionRow(
                        title: "Signal<T>",
                        subtitle: "Mutable state that records who read it"
                    ),
                    new AdwActionRow(
                        title: "Computed<T>",
                        subtitle: "Derived, cached, invalidated by its sources"
                    ),
                    new AdwActionRow(
                        title: "Watch",
                        subtitle: "A subtree rebuilt from the signals it read"
                    ),
                    new AdwActionRow(
                        title: "Effect",
                        subtitle: "Side effects, re-run on change and disposed with the widget"
                    )
                ),
            },
        };
    }
}
