using Zigote.Core;
using Zigote.Core.State;
using Zigote.UI.Material;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using static Gallery.GalleryUi;

namespace Gallery;

/// <summary>
///     Selection controls — the page-level state exemplar: all state lives in an immutable
///     <see cref="SelectionState" /> record inside a <see cref="SelectionStore" />'s
///     <c>Signal&lt;SelectionState&gt;</c>; widgets write intents and the page (a ComposedWidget
///     subscribed to the signal — the same pattern the editor uses) re-renders from the new state.
/// </summary>
internal sealed record SelectionState(
    bool Checked,
    bool SwitchOn,
    int Radio,
    float Slider,
    int Segment,
    float Stepper,
    float Number)
{
    public static SelectionState Initial => new(
        Checked: true,
        SwitchOn: true,
        Radio: 1,
        Slider: 0.4f,
        Segment: 0,
        Stepper: 3,
        Number: 5
    );
}

internal sealed class SelectionStore
{
    public Signal<SelectionState> State { get; } = new(SelectionState.Initial);

    public void SetChecked(bool value) => State.Update(s => s with { Checked = value });

    public void SetSwitch(bool value) => State.Update(s => s with { SwitchOn = value });

    public void SetRadio(int value) => State.Update(s => s with { Radio = value });

    public void SetSlider(float value) => State.Update(s => s with { Slider = value });

    public void SetSegment(int value) => State.Update(s => s with { Segment = value });

    public void SetStepper(float value) => State.Update(s => s with { Stepper = value });

    public void SetNumber(float value) => State.Update(s => s with { Number = value });
}

internal sealed class SelectionPage : ComposedWidget
{
    private readonly SelectionStore _store = new();

    // Retained across rebuilds: recreating these mid-gesture would drop an active drag/scrub.
    // They own their visual value and report through the store like every other control.
    private NumberInput? _number;
    private Slider? _slider;

    protected override void OnMount() => _store.State.Changed += OnStateChanged;

    protected override void OnUnmount() => _store.State.Changed -= OnStateChanged;

    protected override Widget Build(BuildContext context)
    {
        var s = _store.State.Value;

        _slider ??= new Slider(
            value: s.Slider,
            min: 0,
            max: 1,
            onChanged: _store.SetSlider
        );
        _number ??= new NumberInput(s.Number) { OnChanged = v => _store.SetNumber(v) };

        return Sections(
            Section(
                title: "Checkbox & Switch",
                child: new Column(
                    crossAxisAlignment: CrossAxisAlignment.Start,
                    children: [
                        // The tap intent feeds LabeledRow's phone arm, where the whole row — not
                        // the glyph a finger can barely hit — is the target.
                        LabeledRow(
                            control: new Checkbox(value: s.Checked, onChanged: _store.SetChecked),
                            label: s.Checked ? "Checked" : "Unchecked",
                            onTap: () => _store.SetChecked(!s.Checked)
                        ),
                        new SizedBox(height: 8),
                        LabeledRow(
                            control: new Switch(value: s.SwitchOn, onChanged: _store.SetSwitch),
                            label: s.SwitchOn ? "On" : "Off",
                            onTap: () => _store.SetSwitch(!s.SwitchOn)
                        ),
                    ]
                )
            ),
            Section(
                title: "Radio group",
                // Finger-sized radios turn three inline options into most of a phone-width card;
                // Wrap gives the run somewhere to go instead of painting past the card edge.
                child: new AdaptiveBuilder((_, size) => size == WindowSizeClass.Compact
                    ? new Wrap(
                        spacing: 0,
                        runSpacing: 4,
                        children: [
                            Radio(s: s, value: 0, label: "One"),
                            Radio(s: s, value: 1, label: "Two"),
                            Radio(s: s, value: 2, label: "Three"),
                        ]
                    )
                    : new Row(
                        [
                            Radio(s: s, value: 0, label: "One"),
                            Radio(s: s, value: 1, label: "Two"),
                            Radio(s: s, value: 2, label: "Three"),
                        ]
                    )
                )
            ),
            Section(
                title: "Slider",
                child: new Column(
                    crossAxisAlignment: CrossAxisAlignment.Stretch,
                    children: [
                        _slider,
                        new Text($"Value: {s.Slider:F2}"),
                    ]
                )
            ),
            Section(
                title: "Segmented control",
                child: new SegmentedControl(
                    segments: ["Day", "Week", "Month"],
                    selected: s.Segment,
                    onChanged: _store.SetSegment
                )
            ),
            Section(
                title: "Stepper & number",
                child: new Row(
                    [
                        new Stepper(
                            value: s.Stepper,
                            step: 1,
                            min: 0,
                            max: 10,
                            onChanged: _store.SetStepper
                        ),
                        new SizedBox(8),
                        new Text($"{s.Stepper:F0}"),
                        new SizedBox(24),
                        _number,
                        new SizedBox(8),
                        new Text($"{s.Number:F0}"),
                    ]
                )
            )
        );
    }

    private void OnStateChanged(SelectionState _) => MarkNeedsBuild();

    private Widget Radio(SelectionState s, int value, string label)
    {
        return new Padding(
            padding: EdgeInsets.Only(right: 12),
            child: LabeledRow(
                control: new Radio<int>(
                    value: value,
                    groupValue: s.Radio,
                    onChanged: _store.SetRadio
                ),
                label: label,
                onTap: () => _store.SetRadio(value)
            )
        );
    }
}
