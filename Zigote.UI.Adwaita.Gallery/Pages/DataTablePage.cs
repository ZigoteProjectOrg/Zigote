using System.Diagnostics;
using System.Globalization;

namespace AdwaitaGallery.Pages;

/// <summary>
///     Data Table — <see cref="AdwColumnView{T}" /> twice: the processes running on this machine
///     (a real snapshot, sortable, refreshable), and a hundred thousand generated orders to show
///     that only the rows in view cost anything.
/// </summary>
public sealed class DataTablePage : ComposedWidget
{
    private static readonly string[] Customers = [
        "Ada", "Brian", "Chiara", "Dmitri", "Emeka", "Farah", "Goran", "Hana", "Ines", "Jules",
        "Kenji", "Lena",
        "Mateo", "Nadia", "Omar", "Priya", "Quinn", "Rosa", "Sven", "Tamar", "Uma", "Viktor", "Wen",
        "Yusuf",
    ];

    private static readonly string[] States = ["Paid", "Shipped", "Pending", "Refunded"];

    // Generated once: a theme switch rebuilds the page, and that should not cost 100k rows again.
    private static readonly Lazy<List<Order>> Orders = new(() => Order.Generate(100_000));

    private readonly Signal<string> _orderSelection = new("nothing selected");
    private readonly Signal<string> _processInfo = new("");
    private AdwColumnView<ProcessRow>? _processes;

    protected override Widget Build(BuildContext context)
    {
        var host = GalleryHost.Find(context);

        _processes = new AdwColumnView<ProcessRow>(
            items: ProcessRow.Snapshot(),
            new AdwColumn<ProcessRow>(
                title: "Name",
                text: r => r.Name.Length > 0 ? r.Name : "(unnamed)"
            ) {
                Expand = true,
                MinWidth = 160f,
                Icon = _ => MaterialIcons.Memory,
            }.SortBy(r => r.Name),
            new AdwColumn<ProcessRow>(
                title: "PID",
                text: r => r.Id.ToString(CultureInfo.InvariantCulture)
            ) {
                Width = 80f,
                Align = AdwColumnAlign.End,
                Dim = true,
            }.SortBy(r => r.Id),
            new AdwColumn<ProcessRow>(title: "Memory", text: r => FormatBytes(r.Memory)) {
                Width = 110f,
                Align = AdwColumnAlign.End,
            }.SortBy(r => r.Memory),
            new AdwColumn<ProcessRow>(
                title: "Threads",
                text: r => r.Threads < 0 ? "—" : r.Threads.ToString(CultureInfo.InvariantCulture)
            ) {
                Width = 90f,
                Align = AdwColumnAlign.End,
                Dim = true,
            }.SortBy(r => r.Threads)
        ) {
            SemanticsLabel = "Processes",
            OnActivated = r => host?.Toast($"{r.Name} — PID {r.Id}"),
        };
        _processes.SortBy(column: 2, ascending: false); // heaviest first, like a system monitor
        _processInfo.Value = $"{_processes.Items.Count} processes";

        var orders = new AdwColumnView<Order>(
            items: Orders.Value,
            new AdwColumn<Order>(title: "Order", text: o => o.Number) {
                Width = 90f,
                Dim = true,
            }.SortBy(o => o.Id),
            new AdwColumn<Order>(title: "Customer", text: o => o.Customer) {
                Expand = true,
                MinWidth = 80f,
            }.SortBy(o => o.Customer),
            new AdwColumn<Order>(title: "Status", text: o => o.Status) { Width = 96f }.SortBy(o =>
                o.Status
            ),
            new AdwColumn<Order>(
                title: "Date",
                text: o => o.Date.ToString(
                    format: "yyyy-MM-dd",
                    provider: CultureInfo.InvariantCulture
                )
            ) {
                Width = 112f,
                Dim = true,
            }.SortBy(o => o.Date),
            new AdwColumn<Order>(
                title: "Total",
                text: o => o.Total.ToString(format: "C", provider: CultureInfo.CurrentCulture)
            ) {
                Width = 88f,
                Align = AdwColumnAlign.End,
            }.SortBy(o => o.Total)
        ) {
            SelectionMode = AdwSelectionMode.Multiple,
            Compact = true,
            ShowRowSeparators = true,
            SemanticsLabel = "Orders",
        };
        orders.OnSelectionChanged = () =>
        {
            int n = orders.SelectedCount;
            decimal sum = 0m;
            foreach (var o in orders.SelectedItems) sum += o.Total;
            _orderSelection.Value = n == 0
                ? "nothing selected"
                : $"{n:N0} selected · {sum.ToString(format: "C", provider: CultureInfo.CurrentCulture)}";
        };

        return new GalleryPage(
            title: "Data Table",
            description:
            "Columns you can sort and resize over rows that are painted only when you can see them.",
            iconName: MaterialIcons.TableChart
        ) {
            Children = {
                Demo.Titled(
                    title: "Processes",
                    description:
                    "A snapshot of this machine. Click a title to sort, again to reverse; drag its edge to resize. " +
                    "Double-click a row.",
                    child: Demo.Stage(
                        child: new Column(
                            spacing: Spacing.Md,
                            crossAxisAlignment: CrossAxisAlignment.Stretch
                        ) {
                            Children = {
                                Framed(table: _processes, height: 320f),
                                Demo.Bar(
                                    new AdwButton(label: "Refresh", onPressed: Refresh),
                                    new Watch(() => Demo.Value(_processInfo.Value))
                                ),
                            },
                        },
                        padding: Spacing.Lg
                    )
                ),
                Demo.Titled(
                    title: "100,000 Rows",
                    description:
                    "Dense rows with separators and multiple selection: ⌘/Ctrl-click, Shift-click, Shift-arrows, " +
                    "⌘/Ctrl+A. A table this size sorts on a worker thread, so a click on a title never stalls a frame.",
                    child: Demo.Stage(
                        child: new Column(
                            spacing: Spacing.Md,
                            crossAxisAlignment: CrossAxisAlignment.Stretch
                        ) {
                            Children = {
                                Framed(table: orders, height: 360f),
                                new Watch(() => new Align(
                                        alignment: Alignment.Center,
                                        child: Demo.Value(_orderSelection.Value)
                                    ) {
                                        HeightFactor = 1f,
                                    }
                                ),
                            },
                        },
                        padding: Spacing.Lg
                    )
                ),
            },
        };
    }

    private void Refresh()
    {
        if (_processes is null) return;
        int column = _processes.SortColumn;
        bool ascending = _processes.SortAscending;
        _processes.Items = ProcessRow.Snapshot();
        _processes.SortBy(column: column, ascending: ascending);
        _processInfo.Value =
            $"{_processes.Items.Count} processes · refreshed {DateTime.Now:HH:mm:ss}";
    }

    /// <summary>
    ///     A table is a `.view`: give it the card's rounded corners and a hairline. The hairline is
    ///     the frame's own fill showing through a 1px inset — a border drawn by the frame would sit
    ///     under the table's background.
    /// </summary>
    private static Widget Framed(Widget table, float height) =>
        new SizedBox(
            height: height,
            child: new DecoratedBox {
                Radius = AdwMetrics.CardRadius,
                Fill = AdwPalette.For(ThemeProvider.Of(BuildContext.Current)).Border,
                Child = new Padding(
                    padding: EdgeInsets.All(1f),
                    child: new ClipRRect(radius: AdwMetrics.CardRadius - 1f, child: table)
                ),
            }
        );

    private static string FormatBytes(long bytes) => bytes switch {
        < 0 => "—",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.00} GB",
    };

    private sealed record ProcessRow(string Name, int Id, long Memory, int Threads)
    {
        /// <summary>Every process we may look at; fields the OS will not show come back as -1.</summary>
        public static List<ProcessRow> Snapshot()
        {
            var rows = new List<ProcessRow>();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        rows.Add(
                            new ProcessRow(
                                Name: p.ProcessName,
                                Id: p.Id,
                                Memory: Try(() => p.WorkingSet64),
                                Threads: (int)Try(() => p.Threads.Count)
                            )
                        );
                    }
                    catch (InvalidOperationException)
                    {
                        // Exited between the listing and the read.
                    }
                }
            }

            return rows;
        }

        private static long Try(Func<long> read)
        {
            try
            {
                return read();
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException
                                          or System.ComponentModel.Win32Exception
                                          or UnauthorizedAccessException)
            {
                return -1;
            }
        }
    }

    private sealed record Order(
        int Id,
        string Number,
        string Customer,
        string Status,
        DateOnly Date,
        decimal Total)
    {
        /// <summary>Deterministic, so the same rows come back every run.</summary>
        public static List<Order> Generate(int count)
        {
            var random = new Random(42);
            var start = new DateOnly(year: 2025, month: 1, day: 1);
            var list = new List<Order>(count);
            for (int i = 0; i < count; i++)
            {
                list.Add(
                    new Order(
                        Id: i + 1,
                        Number: $"#{100000 + i}",
                        Customer: Customers[random.Next(Customers.Length)],
                        Status: States[random.Next(States.Length)],
                        Date: start.AddDays(random.Next(650)),
                        Total: Math.Round(
                            d: (decimal)((random.NextDouble() * 900) + 5),
                            decimals: 2
                        )
                    )
                );
            }

            return list;
        }
    }
}
