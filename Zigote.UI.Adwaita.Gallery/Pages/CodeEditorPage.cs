using Zigote.Modules.UI.CodeEditor;

namespace AdwaitaGallery.Pages;

/// <summary>
///     The source view: a monospace editor with a line gutter, syntax highlighting, undo and soft
///     wrap. libadwaita has no such widget — GNOME apps reach for GtkSourceView — so this one
///     follows the Text Editor's look.
/// </summary>
public sealed class CodeEditorPage : ComposedWidget
{
    private const string Sample =
        """
        namespace HelloWorld;

        /// <summary>One screen, one piece of state.</summary>
        internal sealed class CounterPage : ComposedWidget
        {
            private readonly Signal<int> _count = new(0);

            protected override Widget Build(BuildContext context)
            {
                // Only the Watch re-runs when the signal changes.
                return new AdwToolbarView(
                    new Center(new Watch(() => new Text($"Count: {_count.Value}")))
                ) { TopBars = { new AdwHeaderBar { Title = "Hello World" } } };
            }
        }
        """;

    private readonly Signal<int> _length = new(Sample.Length);
    private AdwCodeEditor? _editor;

    protected override Widget Build(BuildContext context)
    {
        _editor ??= new AdwCodeEditor(Sample) {
            Tokenizer = Highlighting.CSharp,
            OnChanged = text => _length.Value = text.Length,
        };

        return new GalleryPage(
            title: "Code Editor",
            description: "A source view with a gutter, highlighting, undo and soft wrap.",
            iconName: MaterialIcons.Code
        ) {
            ClampWidth = 860f,
            Children = {
                Demo.Group(
                    title: "Options",
                    description: null,
                    new AdwSwitchRow(
                        title: "Soft wrap",
                        subtitle: "Wrap long lines to the view instead of scrolling sideways",
                        value: false,
                        onChanged: v => _editor.SoftWrap = v
                    ),
                    new AdwSwitchRow(
                        title: "Read only",
                        subtitle: "Selection and copy still work",
                        value: false,
                        onChanged: v => _editor.ReadOnly = v
                    ),
                    new Watch(() => new AdwActionRow("Length") {
                            Suffixes = { Demo.Value($"{_length.Value} chars") },
                        }
                    )
                ),
                // The editor is a bare view; the card around it supplies the frame and the corners.
                Demo.Stage(
                    child: new ClipRRect(
                        radius: AdwMetrics.CardRadius,
                        child: new SizedBox(height: 380f, child: _editor)
                    ),
                    padding: 0f
                ),
            },
        };
    }
}
