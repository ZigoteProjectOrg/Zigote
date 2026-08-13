using Zigote.Core;
using Zigote.UI.Adwaita;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.DevTools.Widgets;

/// <summary>
///     Turns a panel's flat column of widgets into an Adwaita preferences page: every run of adjacent
///     charts and readout/control rows becomes one boxed list (a card with hairlines between its
///     rows),
///     while everything else — section headings, notes, buttons, the panels' own dynamic lists —
///     passes
///     through between the cards.
///     <para>
///         Doing it here rather than in each panel is deliberate: panels stay flat lists of rows (the
///         thing they are actually about) and get the grouped layout for free, and there is exactly
///         one
///         place to change when the grouping rules change. Applied once per panel, in
///         <see cref="DevToolsController.WidgetFor" />, so the row instances panels mutate each frame
///         are
///         the very ones on screen.
///     </para>
/// </summary>
public static class DevPage
{
    /// <summary>
    ///     True for the widgets that belong inside a boxed list. Charts are in: a section is a chart
    ///     plus the readouts that explain it, and drawing those as two separate cards reads as two
    ///     unrelated objects.
    /// </summary>
    private static bool IsRow(Widget w) =>
        w is DevKeyValue or DevToggle or DevStepper or DevMeter or DevChartCard;

    /// <summary>Regroup a panel's built tree. Anything that is not a plain column is left alone.</summary>
    public static Widget Group(Widget body)
    {
        if (body is not Column source) return body;

        var outer = new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min
        );
        List<Widget>? run = null;

        foreach (var child in source.Children)
        {
            if (IsRow(child))
            {
                (run ??= []).Add(child);
                continue;
            }

            Flush();
            outer.Children.Add(child);
        }

        Flush();
        return outer;

        void Flush()
        {
            if (run is null) return;
            var group = new AdwPreferencesGroup();
            group.Rows.AddRange(run);
            outer.Children.Add(
                new Padding(padding: EdgeInsets.Only(bottom: Spacing.Sm), child: group)
            );
            run = null;
        }
    }
}
