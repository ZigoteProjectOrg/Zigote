# Text Input & Forms

Zigote's kernel has no text field of its own; editable controls live in `Zigote.UI.Adwaita`. Under all of them is one editing engine (`AdwTextField`) with selection, undo/redo, clipboard, IME composition, word and line navigation, and right-to-left text.

| Control | Use for |
|---|---|
| `AdwEntry` | A single-line field: `Text`, `Placeholder`, `OnChanged`, `OnSubmitted` (Enter) |
| `AdwSearchEntry` | An entry with a search icon and a clear button |
| `AdwTextField` | The engine itself: multi-line (`maxLines`), password (`obscureText`), decorations |
| `AdwEntryRow` | An entry inside a boxed list, with its title as a floating label |
| `AdwSuggestionEntry` | An entry with a completion popup |
| `AdwSpinButton` / `AdwSpinRow` / `AdwNumberInput` | Numbers with step buttons and typed input |
| `AdwSlider` | A continuous value |
| `AdwSwitch`, `AdwSwitchRow`, `AdwCheckButton`, `AdwToggleGroup` | Booleans and small choices |
| `AdwDropDown`, `AdwComboRow` | One choice from a list |
| `AdwDateButton` + calendar, time pickers | Dates and times |
| `AdwCodeEditor` | Source code (the editors on these pages) |

## Fields

```csharp run height=330
class Demo : ComposedWidget
{
    private readonly Signal<string> _echo = new("");

    protected override Widget Build(BuildContext context) =>
        new Padding(EdgeInsets.All(20), new Column(spacing: 12, crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
            new AdwEntry { Placeholder = "Type, then press Enter", OnChanged = t => _echo.Value = t, OnSubmitted = t => _echo.Value = $"Submitted: {t}" },
            new AdwTextField(decoration: new AdwInputDecoration(hintText: "Password"), obscureText: true),
            new SizedBox(height: 90, child: new AdwTextField(
                decoration: new AdwInputDecoration(hintText: "A multi-line note…"), maxLines: null)),
            new AdwSuggestionEntry("",
                suggest: q => [.. new[] { "Amsterdam", "Berlin", "Lisbon", "London", "Madrid", "Paris", "Prague", "Rome", "Vienna" }
                    .Where(c => q.Length > 0 && c.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                    .Select(c => (c, c))],
                onCommit: c => _echo.Value = $"City: {c}") { Placeholder = "City (try “P”)" },
            new Watch(() => new Label(_echo.Value, fontSize: 13)),
        ]));
}
```

Editing works the way the platform does: ⌘/Ctrl+A, C, X, V, Z and Shift+Z, word jumps with ⌥/Ctrl+arrows, double-click selects a word and triple-click a line. IME composition (Chinese, Japanese, Korean, dead keys) is shown inline.

## A form with validation

Keep the draft in one signal, derive each error with a `Computed`, and enable submit only when the whole form is valid. Errors appear only after the first submit attempt, so the user isn't scolded while still typing.

```csharp run height=520
sealed record Draft(string Name = "", string Email = "", string Password = "", int Age = 30, string Plan = "Free", bool Terms = false);

class Demo : ComposedWidget
{
    private static readonly string[] Plans = ["Free", "Pro", "Team"];
    private readonly Signal<Draft> _draft = new(new Draft());
    private readonly Signal<bool> _tried = new(false);
    private readonly Signal<string> _result = new("");
    private readonly Computed<string?> _nameError, _emailError, _passwordError, _termsError;
    private readonly Computed<bool> _valid;

    public Demo()
    {
        _nameError = Computed.From(() => _draft.Value.Name.Trim().Length < 2 ? "Enter your name" : null);
        _emailError = Computed.From(() => _draft.Value.Email.Contains('@') && _draft.Value.Email.Contains('.') ? null : "Enter a valid email address");
        _passwordError = Computed.From(() => _draft.Value.Password.Length >= 8 ? null : "At least 8 characters");
        _termsError = Computed.From(() => _draft.Value.Terms ? null : "Please accept the terms");
        _valid = Computed.From(() => _nameError.Value is null && _emailError.Value is null && _passwordError.Value is null && _termsError.Value is null);
    }

    private void Edit(Func<Draft, Draft> change) => _draft.Value = change(_draft.Peek());

    // An error line under a field: shown only after the first submit attempt.
    private Widget Error(Computed<string?> error, Color color) =>
        new Watch(() => _tried.Value && error.Value is { } msg ? new Label(text: msg, style: AdwTypography.Caption, color: color) : SizedBox.Shrink());

    private void Submit()
    {
        _tried.Value = true;
        if (!_valid.Peek()) { _result.Value = ""; return; }
        var d = _draft.Peek();
        _result.Value = $"✓ Welcome, {d.Name}! {d.Plan} plan, age {d.Age}.";
    }

    protected override Widget Build(BuildContext context)
    {
        var red = ThemeProvider.Of(context).Error;
        return new SingleChildScrollView {
            Child = new Padding(EdgeInsets.All(20), new Column(spacing: 8, mainAxisSize: MainAxisSize.Min, crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
                new AdwTextField(decoration: new AdwInputDecoration(labelText: "Name"), onChanged: v => Edit(d => d with { Name = v })),
                Error(_nameError, red),
                new AdwTextField(decoration: new AdwInputDecoration(labelText: "Email"), onChanged: v => Edit(d => d with { Email = v })),
                Error(_emailError, red),
                new AdwTextField(decoration: new AdwInputDecoration(labelText: "Password"), obscureText: true, onChanged: v => Edit(d => d with { Password = v })),
                Error(_passwordError, red),
                new Row(spacing: 12, children: [
                    new Label("Age", fontSize: 14),
                    new AdwSpinButton(value: 30, min: 13, max: 120, onChanged: v => Edit(d => d with { Age = (int)v })),
                    new Label("Plan", fontSize: 14),
                    new AdwDropDown(Plans, onSelected: i => Edit(d => d with { Plan = Plans[i] })),
                ]),
                new AdwCheckButton(label: "I accept the terms", onChanged: v => Edit(d => d with { Terms = v })),
                Error(_termsError, red),
                new Row(spacing: 12, children: [
                    new AdwButton(label: "Create account", onPressed: Submit) { Style = AdwButtonStyle.Suggested, Pill = true },
                    new Watch(() => new Label(_valid.Value ? "Ready" : "Incomplete", fontSize: 12)),
                ]),
                new Watch(() => new Label(_result.Value, fontSize: 15)),
            ])),
        };
    }
}
```

Why this shape works:

- **One draft record** in one signal. Every field edits it with `with`, so the whole form state is a single immutable value you can save, compare or reset.
- **Errors are derived** with `Computed`, never stored, so they can't disagree with the data.
- **Each error line is its own small `Watch`.** Typing in one field repaints one caption, not the form.
- The fields themselves are **never rebuilt**, so they keep their caret, selection and undo history.

## Keeping controls in sync with state

Most controls take an initial value and report changes through a callback (`onChanged`, `OnSubmitted`). To push a value *into* a control later (a "Reset" button, data loaded from disk), keep the control in a field and set its property, `_entry.Text = ""`, rather than rebuilding it. A rebuilt field loses its caret and undo history.

## Tips

- Use `AdwEntryRow` and the other `*Row` controls inside `AdwPreferencesGroup` for settings screens. They are the GNOME idiom and handle focus rings and keyboard navigation for you.
- Tab and Shift+Tab move between fields; Enter fires `OnSubmitted`. Wire Enter on the last field to your submit action.
- Validate on submit or on blur, not on every keystroke, unless the feedback is positive (a password-strength meter).
- On mobile, focusing a field shows the on-screen keyboard and pans the view so the field stays visible. Nothing extra is needed.
