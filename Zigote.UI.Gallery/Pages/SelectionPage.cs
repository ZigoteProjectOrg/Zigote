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
///     <c>Signal&lt;SelectionState&gt;</c>; widgets write intents and the page (a StatefulWidget
///     subscribed to the signal — the same pattern the editor uses) re-renders from the new state.
/// </summary>
internal sealed class SelectionPage : StatefulWidget
{
    protected override WidgetState CreateState()
    {
        return new SelectionPageState();
    }
}

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
        true,
        true,
        1,
        0.4f,
        0,
        3,
        5
    );
}

internal sealed class SelectionStore
{
    public Signal<SelectionState> State { get; } = new(SelectionState.Initial);

    public void SetChecked(bool value)
    {
        State.Update(s => s with { Checked = value });
    }

    public void SetSwitch(bool value)
    {
        State.Update(s => s with { SwitchOn = value });
    }

    public void SetRadio(int value)
    {
        State.Update(s => s with { Radio = value });
    }

    public void SetSlider(float value)
    {
        State.Update(s => s with { Slider = value });
    }

    public void SetSegment(int value)
    {
        State.Update(s => s with { Segment = value });
    }

    public void SetStepper(float value)
    {
        State.Update(s => s with { Stepper = value });
    }

    public void SetNumber(float value)
    {
        State.Update(s => s with { Number = value });
    }
}

internal sealed class SelectionPageState : WidgetState<SelectionPage>
{
    private readonly SelectionStore _store = new();

    // Retained across rebuilds: recreating these mid-gesture would drop an active drag/scrub.
    // They own their visual value and report through the store like every other control.
    private NumberInput? _number;
    private Slider? _slider;

    public override void InitState()
    {
        base.InitState();
        _store.State.Changed += OnStateChanged;
    }

    public override void Dispose()
    {
        _store.State.Changed -= OnStateChanged;
        base.Dispose();
    }

    public override Widget Build(BuildContext context)
    {
        var s = _store.State.Value;

        _slider ??= new Slider(
            s.Slider,
            0,
            1,
            _store.SetSlider
        );
        _number ??= new NumberInput(s.Number) { OnChanged = v => _store.SetNumber(v) };

        return Sections(
            Section(
                "Checkbox & Switch",
                new Column(
                    crossAxisAlignment: CrossAxisAlignment.Start,
                    children: [
                        LabeledRow(
                            new Checkbox(s.Checked, _store.SetChecked),
                            s.Checked ? "Checked" : "Unchecked"
                        ),
                        new SizedBox(height: 8),
                        LabeledRow(
                            new Switch(s.SwitchOn, _store.SetSwitch),
                            s.SwitchOn ? "On" : "Off"
                        ),
                    ]
                )
            ),
            Section(
                "Radio group",
                new Row(
                    [
                        Radio(s, 0, "One"), Radio(s, 1, "Two"), Radio(s, 2, "Three"),
                    ]
                )
            ),
            Section(
                "Slider",
                new Column(
                    crossAxisAlignment: CrossAxisAlignment.Stretch,
                    children: [
                        _slider,
                        new Text($"Value: {s.Slider:F2}"),
                    ]
                )
            ),
            Section(
                "Segmented control",
                new SegmentedControl(
                    ["Day", "Week", "Month"],
                    s.Segment,
                    _store.SetSegment
                )
            ),
            Section(
                "Stepper & number",
                new Row(
                    [
                        new Stepper(
                            s.Stepper,
                            1,
                            0,
                            10,
                            _store.SetStepper
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

    private void OnStateChanged(SelectionState _)
    {
        SetStateRebuild(() => { });
    }

    private Widget Radio(SelectionState s, int value, string label)
    {
        return new Padding(
            EdgeInsets.Only(right: 12),
            LabeledRow(new Radio<int>(value, s.Radio, _store.SetRadio), label)
        );
    }
}