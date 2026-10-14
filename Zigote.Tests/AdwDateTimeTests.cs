using Xunit;
using Zigote.Core.Events;
using Zigote.UI.Adwaita;
using Zigote.UI.TextShaping;

namespace Zigote.Tests;

/// <summary>The calendar, the time picker and the column view, driven the way a keyboard drives them.</summary>
public class AdwDateTimeTests
{
    private static void Key(Zigote.UI.Widgets.Widget w, KeyCode key, Modifiers mods = default) =>
        w.OnKey(keyChar: '\0', scancode: (uint)key, down: true, mods: mods);

    [Fact]
    public void CalendarKeysMoveTheDayAndFollowItAcrossMonths()
    {
        var seen = new List<DateOnly>();
        var cal = new AdwCalendar(value: new DateOnly(2026, 10, 31), onChanged: seen.Add);

        Key(cal, KeyCode.Right);
        Assert.Equal(new DateOnly(2026, 11, 1), cal.Value);
        Assert.Equal(new DateOnly(2026, 11, 1), cal.DisplayedMonth);

        Key(cal, KeyCode.Up);
        Assert.Equal(new DateOnly(2026, 10, 25), cal.Value);

        Key(cal, KeyCode.PageDown, Modifiers.Shift); // a year
        Assert.Equal(new DateOnly(2027, 10, 25), cal.Value);

        Key(cal, KeyCode.End);
        Assert.Equal(new DateOnly(2027, 10, 31), cal.Value);
        Assert.Equal(4, seen.Count);
    }

    [Fact]
    public void CalendarNeverSelectsOutsideItsRange()
    {
        var cal = new AdwCalendar(value: new DateOnly(2026, 10, 14)) {
            MinDate = new DateOnly(2026, 10, 10),
            MaxDate = new DateOnly(2026, 10, 20),
        };

        Key(cal, KeyCode.PageUp);
        Assert.Equal(new DateOnly(2026, 10, 10), cal.Value);
        Key(cal, KeyCode.PageDown);
        Assert.Equal(new DateOnly(2026, 10, 20), cal.Value);
    }

    [Fact]
    public void CalendarActivatesOnEnterAfterChanging()
    {
        DateOnly? activated = null;
        var cal = new AdwCalendar(value: new DateOnly(2026, 1, 1)) { OnDayActivated = d => activated = d };
        Key(cal, KeyCode.Down);
        Assert.Null(activated); // moving is not picking
        Key(cal, KeyCode.Enter);
        Assert.Equal(new DateOnly(2026, 1, 8), activated);
    }

    [Fact]
    public void TimePickerWrapsWithoutCarrying()
    {
        var picker = new AdwTimePicker(new TimeOnly(23, 59)) { Use24Hour = true };
        picker.Step(segment: 1, dir: 1);
        Assert.Equal(new TimeOnly(23, 0), picker.Value); // minutes wrap, the hour stays
        picker.Step(segment: 0, dir: 1);
        Assert.Equal(new TimeOnly(0, 0), picker.Value);
    }

    [Fact]
    public void TimePickerMinuteStepSnapsToItsGrid()
    {
        var picker = new AdwTimePicker(new TimeOnly(10, 7)) { MinuteStep = 5 };
        picker.Step(segment: 1, dir: 1);
        Assert.Equal(10, picker.Value.Minute);
        picker.Step(segment: 1, dir: -1);
        picker.Step(segment: 1, dir: -1);
        Assert.Equal(0, picker.Value.Minute);
        picker.Step(segment: 1, dir: -1);
        Assert.Equal(55, picker.Value.Minute);
    }

    [Fact]
    public void TypingFillsHoursThenMinutes()
    {
        var picker = new AdwTimePicker(new TimeOnly(12, 0)) { Use24Hour = true };
        picker.OnTextInput("0730");
        Assert.Equal(new TimeOnly(7, 30), picker.Value);

        // A digit no two-digit hour can start with is a whole hour on its own.
        picker.OnKey('\0', (uint)KeyCode.Left, true, default);
        picker.OnTextInput("9");
        Assert.Equal(new TimeOnly(9, 30), picker.Value);
    }

    [Fact]
    public void TwelveHourTypingKeepsTheHalfOfTheDay()
    {
        var picker = new AdwTimePicker(new TimeOnly(15, 0)) { Use24Hour = false };
        picker.OnTextInput("11");
        Assert.Equal(new TimeOnly(23, 0), picker.Value); // still PM
        picker.OnTextInput("00a");
        Assert.Equal(new TimeOnly(11, 0), picker.Value);
    }

    private sealed record Row(string Name, int Size);

    private static AdwColumnView<Row> Table(params Row[] rows) => new(
        rows,
        new AdwColumn<Row>(title: "Name", text: r => r.Name).SortBy(r => r.Name),
        new AdwColumn<Row>(title: "Size", text: r => r.Size.ToString()).SortBy(r => r.Size)
    ) { SelectionMode = AdwSelectionMode.Multiple };

    [Fact]
    public void ColumnViewSortsStablyAndKeepsTheSelectionOnItsRows()
    {
        var table = Table(new Row("b", 2), new Row("a", 2), new Row("c", 1));
        table.Select([0, 2]); // "b" and "c"

        table.SortBy(column: 1); // by size; "b" and "a" tie and keep their order
        Assert.Equal(["c", "b"], table.SelectedItems.Select(r => r.Name));

        table.SortBy(column: 0, ascending: false);
        Assert.Equal(["c", "b"], table.SelectedItems.Select(r => r.Name));
        Assert.Equal(0, table.SortColumn);
    }

    [Fact]
    public void ColumnViewIgnoresSortOnAnUnsortableColumn()
    {
        var table = new AdwColumnView<Row>([new Row("a", 1)], new AdwColumn<Row>(title: "Name", text: r => r.Name));
        table.SortBy(column: 0);
        Assert.Equal(-1, table.SortColumn);
    }

    [Fact]
    public void NewItemsClearTheSelection()
    {
        var table = Table(new Row("a", 1));
        table.Select([0]);
        int changes = 0;
        table.OnSelectionChanged = () => changes++;
        table.Items = [new Row("z", 9)];
        Assert.Equal(0, table.SelectedCount);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void EllipsizeFitsTheRoomAndKeepsAPrefix()
    {
        const float fs = 14f;
        string text = "com.apple.WebKit.WebContent.Captive";
        float room = TextMeasure.Width(text: text, fontSize: fs) / 2f;
        string cut = AdwColumnView<Row>.Ellipsize(text: text, room: room, fs: fs);
        Assert.EndsWith("…", cut);
        Assert.StartsWith(cut[..^1], text);
        Assert.True(TextMeasure.Width(text: cut, fontSize: fs) <= room + 1f);
        Assert.Equal("…", AdwColumnView<Row>.Ellipsize(text: text, room: 1f, fs: fs));
    }
}
