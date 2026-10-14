namespace AdwaitaGallery.Pages;

/// <summary>
///     Date &amp; Time — the calendar, the clock-style time picker, the compact buttons that open
///     them in a popover, and the rows a preferences or event form is built from.
/// </summary>
public sealed class DateTimePage : ComposedWidget
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private readonly Signal<DateOnly> _date = new(Today);
    private readonly Signal<DateOnly> _ends = new(Today.AddDays(2));
    private readonly Signal<DateOnly> _starts = new(Today);
    private readonly Signal<TimeOnly> _time = new(new TimeOnly(hour: 9, minute: 30));

    // A few "events" this month, so the calendar's marks have something to show.
    private static bool HasEvent(DateOnly day) => day.Day % 9 == 3 || day == Today.AddDays(3);

    protected override Widget Build(BuildContext context)
    {
        var host = GalleryHost.Find(context);

        return new GalleryPage(
            title: "Date & Time",
            description: "A month at a glance, a clock you can spin, and both folded into a button or a row.",
            iconName: MaterialIcons.CalendarMonth
        ) {
            Children = {
                Demo.Titled(
                    title: "Calendar",
                    description:
                    "Arrows move the day, Page Up/Down the month, the wheel pages. Dots are marked days.",
                    child: Demo.Specimen(
                        new AdwCalendar(value: _date.Peek(), onChanged: d => _date.Value = d) {
                            IsMarked = HasEvent,
                        },
                        new Watch(() => Demo.Value($"date = {_date.Value:yyyy-MM-dd}"))
                    )
                ),
                Demo.Titled(
                    title: "In a Button",
                    description: "The compact form for a toolbar or a dialog: the value, and the picker on click.",
                    child: Demo.Stage(
                        Demo.Bar(
                            new AdwDateButton(value: _date.Peek(), onChanged: d => _date.Value = d) {
                                IsMarked = HasEvent,
                            },
                            new AdwTimeButton(value: _time.Peek(), onChanged: t => _time.Value = t),
                            new Watch(() => Demo.Value($"{_date.Value:ddd d MMM} · {_time.Value:HH:mm}"))
                        )
                    )
                ),
                Demo.Titled(
                    title: "Time Picker",
                    description:
                    "Spin with the arrows or the wheel, or type the digits. The clock follows the locale; " +
                    "either can be forced, and minutes can step by five.",
                    child: Demo.Stage(
                        Demo.Bar(
                            new AdwTimePicker(value: _time.Peek(), onChanged: t => _time.Value = t) {
                                Use24Hour = true,
                                SemanticsLabel = "24-hour time",
                            },
                            new SizedBox(width: Spacing.Xl),
                            new AdwTimePicker(value: new TimeOnly(hour: 18, minute: 45)) {
                                Use24Hour = false,
                                MinuteStep = 5,
                                SemanticsLabel = "12-hour time",
                            }
                        )
                    )
                ),
                Demo.Titled(
                    title: "Booking Window",
                    description: "MinDate and MaxDate: only the next three weeks can be picked.",
                    child: Demo.Specimen(
                        new AdwCalendar(value: Today) {
                            MinDate = Today,
                            MaxDate = Today.AddDays(21),
                        }
                    )
                ),
                Demo.Group(
                    "Rows",
                    "A form in a boxed list. The end date cannot come before the start.",
                    new AdwDateRow(
                        title: "Starts",
                        value: _starts.Peek(),
                        onChanged: d =>
                        {
                            _starts.Value = d;
                            if (_ends.Peek() < d) _ends.Value = d;
                        }
                    ),
                    new AdwTimeRow(title: "At", value: _time.Peek(), onChanged: t => _time.Value = t) {
                        MinuteStep = 15,
                    },
                    new Watch(() => new AdwDateRow(title: "Ends", value: _ends.Value, onChanged: d => _ends.Value = d) {
                            MinDate = _starts.Value,
                        }
                    ),
                    new AdwButtonRow(
                        title: "Add to Calendar",
                        onPressed: () => host?.Toast($"Added {AdwDateFormat.Medium(_starts.Peek())}")
                    )
                ),
            },
        };
    }
}
