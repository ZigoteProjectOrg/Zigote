namespace Zigote.UI.Material;

/// <summary>
///     A mutable holder for a text field's value plus a change
///     notification. A <c>TextField(controller: …)</c> seeds its text from the controller, writes edits
///     back, and follows external <see cref="Text" /> assignments via <see cref="Changed" />.
/// </summary>
public sealed class TextEditingController
{
    private string _text;

    public TextEditingController(string text = "")
    {
        _text = text ?? "";
    }

    /// <summary>The current text. Assigning it fires <see cref="Changed" /> so a bound field updates.</summary>
    public string Text
    {
        get => _text;
        set
        {
            var v = value ?? "";
            if (_text == v) return;
            _text = v;
            Changed?.Invoke(_text);
        }
    }

    /// <summary>Raised whenever <see cref="Text" /> changes.</summary>
    public event Action<string>? Changed;

    public void Clear()
    {
        Text = "";
    }

    /// <summary>Update the text without notifying — used by a bound field writing its own edits back.</summary>
    internal void SetTextSilently(string value)
    {
        _text = value ?? "";
    }
}