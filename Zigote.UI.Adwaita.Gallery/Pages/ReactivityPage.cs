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

    private readonly Signal<int>[] _quantities = [new(1), new(0), new(2)];
    private readonly Signal<bool> _member = new(false);

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
                var sum = 0f;
                for (var i = 0; i < Catalogue.Length; i++)
                    sum += _quantities[i].Value * Catalogue[i].Price;
                return sum;
            }
        );
        _total = Computed.From(() => _subtotal.Value * (_member.Value ? 0.9f : 1f));
    }

    public override void Attach(App owner, Widget? parent)
    {
        base.Attach(owner, parent);
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
            "Order",
            "Each row writes one signal. Nothing here knows about the total."
        );
        for (var i = 0; i < Catalogue.Length; i++)
        {
            var index = i;
            var (name, price) = Catalogue[i];
            order.Rows.Add(
                new AdwActionRow(name, $"{price:0.00} each") {
                    Suffixes = {
                        new AdwSpinButton(
                            _quantities[index].Peek(),
                            0,
                            9,
                            1,
                            v => _quantities[index].Value = (int)v
                        ),
                    },
                }
            );
        }

        return new GalleryPage(
            "Reactivity",
            "Signals in, derived values out, and a widget tree that subscribes to exactly what it reads.",
            MaterialIcons.Bolt
        ) {
            ClampWidth = 680f,
            Children = {
                order,
                new AdwPreferencesGroup("Discount") {
                    Rows = {
                        new Watch(() => new AdwSwitchRow(
                                "Member",
                                "Takes 10% off the subtotal",
                                _member.Value,
                                v => _member.Value = v
                            )
                        ),
                    },
                },
                Demo.Titled(
                    "Derived",
                    "A Computed caches: the counter only moves when a quantity actually changes.",
                    Demo.Stage(
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
                    "The Same State, Three Ways",
                    "One signal, three unrelated widgets — no shared parent, no callbacks between them.",
                    Demo.Stage(
                        new Column(
                            spacing: Spacing.Lg,
                            mainAxisSize: MainAxisSize.Min,
                            crossAxisAlignment: CrossAxisAlignment.Stretch
                        ) {
                            Children = {
                                new Watch(() => new AdwProgressBar(
                                        Math.Clamp(_total.Value / 20f, 0f, 1f)
                                    )
                                ),
                                new Watch(() => new AdwLevelBar(
                                        Math.Clamp(_total.Value / 20f, 0f, 1f)
                                    )
                                ),
                                new Watch(() => new Align(
                                        Alignment.Center,
                                        new AdwButton(
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
                    "The Pieces",
                    null,
                    new AdwActionRow("Signal<T>", "Mutable state that records who read it"),
                    new AdwActionRow("Computed<T>", "Derived, cached, invalidated by its sources"),
                    new AdwActionRow("Watch", "A subtree rebuilt from the signals it read"),
                    new AdwActionRow(
                        "Effect",
                        "Side effects, re-run on change and disposed with the widget"
                    )
                ),
            },
        };
    }
}
