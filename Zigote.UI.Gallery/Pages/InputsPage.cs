using Zigote.UI.Material;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using static Gallery.GalleryUi;

namespace Gallery;

/// <summary>
///     Text entry and pickers. Keystroke state is ephemeral UI state, so it stays in the widget
///     (per BLoC guidance — cubits hold app state, not caret positions): the field and its echo
///     label are retained and mutated in place so typing never recreates the focused
///     field.
/// </summary>
internal sealed class InputsPage : ComposedWidget
{
    /// <summary>Smallest comfortable finger target; 28 pt controls are mouse-sized.</summary>
    private const float TouchHeight = 44f;

    private readonly Text _typedLabel = new("You typed: ");
    private string _fruit = "Apple";
    private TextField? _typedField;

    protected override Widget Build(BuildContext context)
    {
        var fruits = new List<DropdownMenuItem<string>> {
            new(value: "Apple", child: "Apple"),
            new(value: "Banana", child: "Banana"),
            new(value: "Cherry", child: "Cherry"),
            new(value: "Durian", child: "Durian"),
        };

        // Retain the field + result label and mutate the label directly (relayout, no rebuild)
        // so typing never recreates the field — otherwise the rebuild detaches it and focus is lost
        // after the first character.
        var typed = _typedField ??= new TextField(
            decoration: new InputDecoration("Type something…"),
            onChanged: v =>
            {
                _typedLabel.Text = $"You typed: {v}";
                MarkNeedsLayout();
            }
        );

        // Single-line fields are the page's primary targets, so they take the touch height on a
        // phone (the multi-line field sizes from its row count and is already tall enough).
        return new AdaptiveBuilder((_, size) =>
            {
                float fieldHeight = size == WindowSizeClass.Compact
                    ? TouchHeight
                    : ControlMetrics.RegularHeight;
                typed.Height = fieldHeight;

                return Sections(
                    Section(
                        title: "Text field",
                        child: new Column(
                            crossAxisAlignment: CrossAxisAlignment.Start,
                            children: [
                                typed,
                                new SizedBox(height: 8),
                                _typedLabel,
                            ]
                        )
                    ),
                    Section(
                        title: "Multiline / read-only / obscured",
                        child: new Column(
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
                                ) { Height = fieldHeight },
                                new SizedBox(height: 8),
                                new TextField(
                                    decoration: new InputDecoration("Read-only"),
                                    readOnly: true
                                ) { Height = fieldHeight },
                            ]
                        )
                    ),
                    Section(
                        title: "Search field",
                        child: new SearchField(hint: "Search…", onChanged: _ => { }) {
                            Height = fieldHeight,
                        }
                    ),
                    Section(
                        title: "Dropdown",
                        child: new DropdownButton<string>(
                            items: fruits,
                            value: _fruit,
                            onChanged: v =>
                            {
                                _fruit = v ?? "Apple";
                                MarkNeedsBuild();
                            }
                        )
                    )
                );
            }
        );
    }
}
