using Xunit;
using Zigote.Core.Events;
using Zigote.UI.Adwaita;
using Zigote.UI.TextShaping;

namespace Zigote.Tests;

/// <summary>The calendar, the time picker and the column view, driven the way a keyboard drives them.</summary>
public class AdwDateTimeTests
{
    private static void Key(UI.Widgets.Widget w, KeyCode key, Modifiers mods = default) =>
        w.OnKey(
            keyChar: '\0',
            scancode: (uint)key,
            down: true,
            mods: mods
        );

    [Fact]
    public void CalendarKeysMoveTheDayAndFollowItAcrossMonths()
    {
        var seen = new List<DateOnly>();
        var cal = new AdwCalendar(
            value: new DateOnly(year: 2026, month: 10, day: 31),
            onChanged: seen.Add
        );

        Key(w: cal, key: KeyCode.Right);
        Assert.Equal(expected: new DateOnly(year: 2026, month: 11, day: 1), actual: cal.Value);
        Assert.Equal(
            expected: new DateOnly(year: 2026, month: 11, day: 1),
            actual: cal.DisplayedMonth
        );

        Key(w: cal, key: KeyCode.Up);
        Assert.Equal(expected: new DateOnly(year: 2026, month: 10, day: 25), actual: cal.Value);

        Key(w: cal, key: KeyCode.PageDown, mods: Modifiers.Shift); // a year
        Assert.Equal(expected: new DateOnly(year: 2027, month: 10, day: 25), actual: cal.Value);

        Key(w: cal, key: KeyCode.End);
        Assert.Equal(expected: new DateOnly(year: 2027, month: 10, day: 31), actual: cal.Value);
        Assert.Equal(expected: 4, actual: seen.Count);
    }

    [Fact]
    public void CalendarNeverSelectsOutsideItsRange()
    {
        var cal = new AdwCalendar(value: new DateOnly(year: 2026, month: 10, day: 14)) {
            MinDate = new DateOnly(year: 2026, month: 10, day: 10),
            MaxDate = new DateOnly(year: 2026, month: 10, day: 20),
        };

        Key(w: cal, key: KeyCode.PageUp);
        Assert.Equal(expected: new DateOnly(year: 2026, month: 10, day: 10), actual: cal.Value);
        Key(w: cal, key: KeyCode.PageDown);
        Assert.Equal(expected: new DateOnly(year: 2026, month: 10, day: 20), actual: cal.Value);
    }

    [Fact]
    public void CalendarActivatesOnEnterAfterChanging()
    {
        DateOnly? activated = null;
        var cal = new AdwCalendar(value: new DateOnly(year: 2026, month: 1, day: 1)) {
            OnDayActivated = d => activated = d,
        };
        Key(w: cal, key: KeyCode.Down);
        Assert.Null(activated); // moving is not picking
        Key(w: cal, key: KeyCode.Enter);
        Assert.Equal(expected: new DateOnly(year: 2026, month: 1, day: 8), actual: activated);
    }

    [Fact]
    public void TimePickerWrapsWithoutCarrying()
    {
        var picker = new AdwTimePicker(new TimeOnly(hour: 23, minute: 59)) { Use24Hour = true };
        picker.Step(segment: 1, dir: 1);
        Assert.Equal(
            expected: new TimeOnly(hour: 23, minute: 0),
            actual: picker.Value
        ); // minutes wrap, the hour stays
        picker.Step(segment: 0, dir: 1);
        Assert.Equal(expected: new TimeOnly(hour: 0, minute: 0), actual: picker.Value);
    }

    [Fact]
    public void TimePickerMinuteStepSnapsToItsGrid()
    {
        var picker = new AdwTimePicker(new TimeOnly(hour: 10, minute: 7)) { MinuteStep = 5 };
        picker.Step(segment: 1, dir: 1);
        Assert.Equal(expected: 10, actual: picker.Value.Minute);
        picker.Step(segment: 1, dir: -1);
        picker.Step(segment: 1, dir: -1);
        Assert.Equal(expected: 0, actual: picker.Value.Minute);
        picker.Step(segment: 1, dir: -1);
        Assert.Equal(expected: 55, actual: picker.Value.Minute);
    }

    [Fact]
    public void TypingFillsHoursThenMinutes()
    {
        var picker = new AdwTimePicker(new TimeOnly(hour: 12, minute: 0)) { Use24Hour = true };
        picker.OnTextInput("0730");
        Assert.Equal(expected: new TimeOnly(hour: 7, minute: 30), actual: picker.Value);

        // A digit no two-digit hour can start with is a whole hour on its own.
        picker.OnKey(
            keyChar: '\0',
            scancode: (uint)KeyCode.Left,
            down: true,
            mods: default
        );
        picker.OnTextInput("9");
        Assert.Equal(expected: new TimeOnly(hour: 9, minute: 30), actual: picker.Value);
    }

    [Fact]
    public void TwelveHourTypingKeepsTheHalfOfTheDay()
    {
        var picker = new AdwTimePicker(new TimeOnly(hour: 15, minute: 0)) { Use24Hour = false };
        picker.OnTextInput("11");
        Assert.Equal(expected: new TimeOnly(hour: 23, minute: 0), actual: picker.Value); // still PM
        picker.OnTextInput("00a");
        Assert.Equal(expected: new TimeOnly(hour: 11, minute: 0), actual: picker.Value);
    }

    private sealed record Row(string Name, int Size);

    private static AdwColumnView<Row> Table(params Row[] rows) => new(
        items: rows,
        new AdwColumn<Row>(title: "Name", text: r => r.Name).SortBy(r => r.Name),
        new AdwColumn<Row>(title: "Size", text: r => r.Size.ToString()).SortBy(r => r.Size)
    ) { SelectionMode = AdwSelectionMode.Multiple };

    [Fact]
    public void ColumnViewSortsStablyAndKeepsTheSelectionOnItsRows()
    {
        var table = Table(
            new Row(Name: "b", Size: 2),
            new Row(Name: "a", Size: 2),
            new Row(Name: "c", Size: 1)
        );
        table.Select([0, 2]); // "b" and "c"

        table.SortBy(column: 1); // by size; "b" and "a" tie and keep their order
        Assert.Equal(expected: ["c", "b"], actual: table.SelectedItems.Select(r => r.Name));

        table.SortBy(column: 0, ascending: false);
        Assert.Equal(expected: ["c", "b"], actual: table.SelectedItems.Select(r => r.Name));
        Assert.Equal(expected: 0, actual: table.SortColumn);
    }

    [Fact]
    public void ColumnViewIgnoresSortOnAnUnsortableColumn()
    {
        var table = new AdwColumnView<Row>(
            items: [new Row(Name: "a", Size: 1)],
            new AdwColumn<Row>(title: "Name", text: r => r.Name)
        );
        table.SortBy(column: 0);
        Assert.Equal(expected: -1, actual: table.SortColumn);
    }

    [Fact]
    public void NewItemsClearTheSelection()
    {
        var table = Table(new Row(Name: "a", Size: 1));
        table.Select([0]);
        int changes = 0;
        table.OnSelectionChanged = () => changes++;
        table.Items = [new Row(Name: "z", Size: 9)];
        Assert.Equal(expected: 0, actual: table.SelectedCount);
        Assert.Equal(expected: 1, actual: changes);
    }

    [Fact]
    public void EllipsizeFitsTheRoomAndKeepsAPrefix()
    {
        const float fs = 14f;
        string text = "com.apple.WebKit.WebContent.Captive";
        float room = TextMeasure.Width(text: text, fontSize: fs) / 2f;
        string cut = AdwColumnView<Row>.Ellipsize(text: text, room: room, fs: fs);
        Assert.EndsWith(expectedEndString: "…", actualString: cut);
        Assert.StartsWith(expectedStartString: cut[..^1], actualString: text);
        Assert.True(TextMeasure.Width(text: cut, fontSize: fs) <= room + 1f);
        Assert.Equal(
            expected: "…",
            actual: AdwColumnView<Row>.Ellipsize(text: text, room: 1f, fs: fs)
        );
    }
}
