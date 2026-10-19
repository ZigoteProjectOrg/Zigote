# Keyboard & Focus

Every Zigote app is fully usable from the keyboard: Tab order, arrow-key navigation inside groups, Enter/Space activation, Esc to dismiss, and app-wide shortcuts.

## Focus

One widget at a time has **keyboard focus**. Key events go to it first, then bubble to its ancestors.

- A widget joins the focus order by returning `true` from `Focusable`.
- **Tab** and **Shift+Tab** walk the focusable widgets in tree order. Focus inside an overlay (a dialog, a popover) is trapped there until it closes.
- Clicking a focusable widget focuses it. Clicking empty space clears focus.
- The **focus ring** appears only for keyboard navigation, not after mouse clicks, matching platform behaviour.
- Read `Focused` to draw focus state; override `OnFocusChanged(bool)` to react to it.
- Move focus from code with `Owner.RequestFocus(widget)`; `AdwEntry.Focus()` does it for entries.

## Handling keys in your own widget

Override `OnKey(char keyChar, uint scancode, bool down, Modifiers mods)`. `scancode` compares to `KeyCode`, and `keyChar` is the printable character when there is one. Return `true` from `HandlesDirectionalKeys` if arrow keys should stay with your widget instead of moving focus.

Click the grid (or Tab to it), then use the arrow keys to move and Space to toggle a cell:

```csharp run height=330
sealed class Grid : LeafWidget
{
    private const int N = 8;
    private readonly bool[,] _on = new bool[N, N];
    private int _x, _y;
    private Size _size;

    public override bool Focusable => true;
    public override bool HandlesDirectionalKeys => true;   // arrows move the cursor, not focus

    public override Size Measure(Constraints c) => _size = new Size(N * 34, N * 34);
    public override void Layout(Offset origin) => Bounds = new Rect(origin.X, origin.Y, _size.Width, _size.Height);

    protected override void OnFocusChanged(bool focused) => MarkNeedsPaint();

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (!down) return;
        switch ((KeyCode)scancode)
        {
            case KeyCode.Left: _x = (_x + N - 1) % N; break;
            case KeyCode.Right: _x = (_x + 1) % N; break;
            case KeyCode.Up: _y = (_y + N - 1) % N; break;
            case KeyCode.Down: _y = (_y + 1) % N; break;
            case KeyCode.Space: _on[_x, _y] = !_on[_x, _y]; break;
            default: return;
        }
        MarkNeedsPaint();
    }

    public override void OnPointerDown(Offset p)
    {
        Owner?.RequestFocus(this);
        _x = Math.Clamp((int)((p.X - Bounds.X) / 34), 0, N - 1);
        _y = Math.Clamp((int)((p.Y - Bounds.Y) / 34), 0, N - 1);
        _on[_x, _y] = !_on[_x, _y];
        MarkNeedsPaint();
    }

    public override void Paint(PaintList paint)
    {
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            var cell = new Rect(Bounds.X + x * 34 + 2, Bounds.Y + y * 34 + 2, 30, 30);
            paint.AddRect(cell, _on[x, y] ? new Color(0xFF3584E4) : new Color(0x22808080), radius: 6);
        }
        if (Focused)   // draw the cursor only while we have focus
            paint.AddBorder(new Rect(Bounds.X + _x * 34, Bounds.Y + _y * 34, 34, 34), new Color(0xFFE66100), radius: 8, width: 3);
    }
}

class Demo : ComposedWidget
{
    protected override Widget Build(BuildContext context) =>
        new Center(new Column(mainAxisSize: MainAxisSize.Min, spacing: 14, children: [
            new Row(mainAxisSize: MainAxisSize.Min, spacing: 8, children: [
                new AdwButton(label: "Before", onPressed: () => { }),
                new AdwButton(label: "After", onPressed: () => { }),
            ]),
            new Grid(),
            new Label("Tab moves between the buttons and the grid. Arrows and Space work inside it.", fontSize: 12),
        ]));
}
```

## Shortcuts

App-wide shortcuts don't belong in a widget's `OnKey`. Bind chords to action names on the window's `Keymap` and handle them in one place:

```csharp
app.Keymap.Bind(action: "doc.save", chord: KeyChord.Command(KeyCode.S));          // ⌘S / Ctrl+S
app.Keymap.Bind(action: "doc.find", chord: KeyChord.Command(KeyCode.F));
app.Keymap.Bind(action: "view.zoom-in", chord: KeyChord.Command(KeyCode.Equals, shift: true));

app.OnShortcut = action =>
{
    // A focused editor may want ⌘F for itself: let it.
    if (action == "doc.find" && app.FocusedWidget is AdwCodeEditor) return false;
    return action switch {
        "doc.save" => Save(),
        "doc.find" => ShowFind(),
        _ => false,                       // false = not handled, the key continues to the focused widget
    };
};
```

`KeyChord.Command` means ⌘ on macOS and Ctrl elsewhere. List shortcuts for users with `AdwShortcutsDialog`, and show a chord next to a menu item with `AdwShortcutLabel`.

On macOS, the native menu bar takes ⌘-shortcuts before the window sees them. Zigote's menu actions are focus-aware, so ⌘C, ⌘V and ⌘Z still reach a focused text field.

## Text input and IME

Printable text doesn't arrive through `OnKey`; it arrives through `OnTextInput(string)`, already composed by the OS input method. Widgets that edit text implement `ITextInputClient`, which tells the OS where the caret is so the IME candidate window appears next to it, and receive in-progress composition through `OnTextComposition`. Use `AdwTextField` instead of writing this yourself.

## Accessibility checklist

- Every action reachable with the mouse is reachable with the keyboard.
- Focus is always visible during keyboard navigation, and never lands on something invisible.
- Esc closes the topmost overlay (dialogs, popovers and menus do this already).
- Custom controls describe themselves through `DescribeSemantics`, giving a role, a label and a value, so assistive technology and the inspect protocol can read them.
