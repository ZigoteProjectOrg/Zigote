using Zigote.Core.Paint;
using Zigote.UI.Theme;

namespace Zigote.UI.Widgets.Controls;

/// <summary>
///     A run of styled text. An alias over <see cref="Label" /> that takes a
///     <see cref="TextStyle" /> plus <c>textAlign</c>/<c>maxLines</c>/<c>overflow</c>:
///     <c>new Text("Hello", style: new TextStyle(fontSize: 20, color: Colors.Red))</c>.
///     <para>
///         Only Left/Center/Right alignment is honoured (Start/End/Justify degrade); overflow supports
///         Clip and Ellipsis (fade/visible are not modelled). <c>softWrap: false</c> forces a single
///         unwrapped line resolved by <c>overflow</c>.
///     </para>
/// </summary>
public class Text : Label
{
    public Text(
        string data,
        TextStyle? style = null,
        TextAlign? textAlign = null,
        int? maxLines = null,
        TextOverflow? overflow = null,
        bool? softWrap = null) : base(data)
    {
        if (style is { } s)
        {
            FontSize = s.Size;
            FontWeight = s.Weight;
            FontStyle = s.Style;
            LineHeight = s.LineHeight;
            LetterSpacing = s.LetterSpacing;
            if (s.FontFamily is not null) FontFamily = s.FontFamily;
            if (s.Color is { } c) Color = c;
            Shadow = s.Shadow;
        }

        if (textAlign is { } ta) Align = ta;
        if (maxLines is { } ml) MaxLines = ml;
        if (overflow is { } ov) Overflow = ov;
        // Label wraps whenever MaxLines != 1, so "don't wrap" maps onto the single-line path.
        if (softWrap == false) MaxLines = 1;
    }
}
