# Using the Playground

Every block marked **Live example** is real C# compiled by Roslyn inside this app. Nothing is pre-rendered.

- **Run**, `⌘↵` or `⌘S` in the editor compiles the code and swaps the preview. With **⚡ Live** on (the default), every pause in typing does.
- Errors are squiggled where they are, with a dot in the gutter; hover either one for the message. The preview lists them, and clicking one jumps to it.
- Completion pops up as you type a name or a `.`, with signatures and doc comments. `Ctrl+Space` opens it anywhere; `↑`/`↓` pick, `↵` or `Tab` insert, `Esc` closes it.
- Parameter hints show while you type a call's arguments, with the argument you are on highlighted and its doc underneath. `↑`/`↓` step through overloads, `Esc` hides them, `Ctrl+Shift+Space` brings them back.
- **Format** (`⌥⌘L`) tidies the code in the docs' style: four-space indentation by nesting, standard C# spacing, Allman braces for types and members, `new X {` initializers kept on their line. It is one undo step, and the caret stays put.
- `Console.WriteLine` in an example prints to a **Console** pane under its preview.
- The toolbar puts the editor **beside the preview** (for every example at once), and flips the preview to the **other theme** or to **right-to-left** to check that your widget follows both.
- **Share** copies a link to the example *with your edits*. The code travels inside the link itself, so no server is involved. To open one, paste it into the search box, or start the playground with it: `dotnet run --project Zigote.Playground -- '<link>'`.
- **↻ Reset** restores the original example.
- Edits are kept while the app runs, so you can switch pages and come back.

The editor itself is `AdwCodeEditor`, the same widget you can put in an app. Its `Markers`, `CompletionProvider`, `SignatureProvider`, `ApplyEdits`, `OnKeyDown` and `GoTo` are how this playground plugs Roslyn in.

## The rules of an example

An example is a C# file containing a class named `Demo`. It can be any `Widget` with a parameterless constructor, usually a `ComposedWidget`. You can declare as many other classes, records and helpers next to it as you like.

These namespaces are imported for you: `Zigote.Core`, `Zigote.Core.State`, `Zigote.Core.Paint`, `Zigote.Core.Animation`, `Zigote.Core.Threading`, `Zigote.UI.Widgets` (with `.Layout`, `.Controls`, `.Transitions` and `.Navigation`), `Zigote.UI.Theme`, `Zigote.UI.Adwaita` and `Zigote.UI.Localizations`, plus the usual `System` ones.

```csharp run height=200
// A helper record, a helper widget and the Demo that uses them.
record Fruit(string Name, string Emoji);

sealed class FruitChip(Fruit fruit) : ComposedWidget
{
    protected override Widget Build(BuildContext context) =>
        new AdwButton(label: $"{fruit.Emoji}  {fruit.Name}", onPressed: () => { }) { Pill = true };
}

class Demo : ComposedWidget
{
    private static readonly Fruit[] Fruits = [new("Apple", "🍎"), new("Pear", "🍐"), new("Kiwi", "🥝")];

    protected override Widget Build(BuildContext context) =>
        new Center(new Wrap(spacing: 8, runSpacing: 8, children: [.. Fruits.Select(f => new FruitChip(f))]));
}
```

## When things go wrong

A compile error is listed under the editor with its `line:column`. An exception is caught wherever it happens (in the constructor, `Build`, layout, paint or an event handler) and shown in place of the preview. The rest of the app keeps running.

Try it: remove a semicolon below and run. Then put the semicolon back and change the code so that `Build` throws.

```csharp run height=140
class Demo : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        string message = "Break me";
        return new Center(new Label(message, fontSize: 20));
    }
}
```

> An infinite loop is the one thing the playground cannot catch: the example runs on the UI thread like any widget. If you write `while (true)`, the window freezes.

## Shared examples

A link opens its example on a page of its own, marked **Shared example**, with a link back to the page it came from. A shared example **does not run until you press Run**: not when it opens, and not while you edit it. An example is ordinary C# and runs with your permissions, so read code from someone else before running it, as you would any program. Completion, errors and formatting all work before you run it, because none of them execute the code.

