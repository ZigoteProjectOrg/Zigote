using Zigote.UI.Material;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using static Gallery.GalleryUi;

namespace Gallery;

/// <summary>
///     Text entry and pickers. Keystroke state is ephemeral UI state, so it stays in the widget
///     (per BLoC guidance — cubits hold app state, not caret positions): the field and its echo
///     label are retained and updated via <c>SetState</c> so typing never recreates the focused
///     field.
/// </summary>
internal sealed class InputsPage : StatefulWidget
{
    protected override WidgetState CreateState()
    {
        return new InputsPageState();
    }
}

internal sealed class InputsPageState : WidgetState<InputsPage>
{
    private readonly Text _typedLabel = new("You typed: ");
    private string _fruit = "Apple";
    private TextField? _typedField;

    public override Widget Build(BuildContext context)
    {
        var fruits = new List<DropdownMenuItem<string>> {
            new("Apple", "Apple"),
            new("Banana", "Banana"),
            new("Cherry", "Cherry"),
            new("Durian", "Durian"),
        };

        // Retain the field + result label and update the label with SetState (relayout, no rebuild)
        // so typing never recreates the field — otherwise the rebuild detaches it and focus is lost
        // after the first character.
        _typedField ??= new TextField(
            decoration: new InputDecoration("Type something…"),
            onChanged: v => SetState(() => _typedLabel.Text = $"You typed: {v}")
        );

        return Sections(
            Section(
                "Text field",
                new Column(
                    crossAxisAlignment: CrossAxisAlignment.Start,
                    children: [
                        _typedField,
                        new SizedBox(height: 8),
                        _typedLabel,
                    ]
                )
            ),
            Section(
                "Multiline / read-only / obscured",
                new Column(
                    crossAxisAlignment: CrossAxisAlignment.Stretch,
                    children: [
                        new TextField(
                            decoration: new InputDecoration("Notes (multi-line)"),
                            maxLines: 3
                        ),
                        new SizedBox(height: 8),
                        new TextField(
                            decoration: new InputDecoration("Password"),
                            obscureText: true
                        ),
                        new SizedBox(height: 8),
                        new TextField(decoration: new InputDecoration("Read-only"), readOnly: true),
                    ]
                )
            ),
            Section("Search field", new SearchField("Search…", _ => { })),
            Section(
                "Dropdown",
                new DropdownButton<string>(
                    fruits,
                    _fruit,
                    v => SetStateRebuild(() => _fruit = v ?? "Apple")
                )
            )
        );
    }
}