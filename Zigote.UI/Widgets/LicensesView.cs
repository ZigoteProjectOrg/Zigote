using Zigote.Core;
using Zigote.Core.Licenses;
using Zigote.UI.Licensing;
using Zigote.UI.Theme;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.Widgets;

/// <summary>
///     A scrollable open-source-licenses screen over <see cref="LicenseRegistry" /> — the
///     LicensePage analogue. Shows every registered attribution (Zigote's own bundled components
///     plus whatever the app added via <see cref="LicenseRegistry.Add" />) as selectable text.
///     Entries registered after the view was first built need an <see cref="Widget.Invalidate" />.
///     For a plain string instead (log, file, custom UI), use
///     <see cref="LicenseRegistry.BuildText" />.
/// </summary>
public class LicensesView : ComposedWidget
{
    private string? _title;

    public LicensesView()
    {
        FontLicenses.EnsureRegistered();
    }

    /// <summary>Optional heading, e.g. the app name.</summary>
    public string? Title
    {
        get => _title;
        set
        {
            _title = value;
            Invalidate();
        }
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = Theme.Of(context);
        var column = new Column();

        if (_title is { Length: > 0 } title)
        {
            column.Children.Add(new Label(title, Typography.Title1, theme.OnBackground));
            column.Children.Add(new SizedBox(height: Spacing.Sm));
        }

        var entries = LicenseRegistry.Collect();
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (i > 0 || _title is not null)
                column.Children.Add(new SizedBox(height: Spacing.Xxl));
            column.Children.Add(
                new Label($"{e.Component} — {e.License}", Typography.Headline, theme.OnBackground)
            );
            if (e.Homepage is { Length: > 0 } url)
                column.Children.Add(new Label(url, Typography.Callout, theme.Hint));
            column.Children.Add(new SizedBox(height: Spacing.Sm));
            column.Children.Add(
                new SelectableText(
                    new TextSpan(e.Text.Trim()) {
                        Color = theme.OnSurface,
                        FontSize = Typography.Callout.Size,
                    }
                )
            );
        }

        return new ScrollView {
            Child = new Padding(EdgeInsets.All(Spacing.Xxl), column),
        };
    }
}