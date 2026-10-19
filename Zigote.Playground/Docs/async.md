# Async & Background Work

There is **one UI thread**. Event dispatch, layout and paint all run on it, so anything slow (disk, network, decoding, heavy maths) belongs on another thread. Zigote makes the return trip easy: **a background thread may write a signal**, and the `Watch` that reads it rebuilds on the UI thread at the next frame.

```csharp run height=260
class Demo : ComposedWidget
{
    private readonly Signal<string> _status = new("Idle");
    private readonly Signal<float> _progress = new(0f);
    private readonly Signal<bool> _busy = new(false);

    private void Start()
    {
        _busy.Value = true;
        Task.Run(async () =>
        {
            for (int i = 1; i <= 20; i++)
            {
                await Task.Delay(100);            // "work", off the UI thread
                _progress.Value = i / 20f;        // signal writes are legal from any thread
                _status.Value = $"Step {i} of 20";
            }
            _status.Value = "Done";
            _busy.Value = false;
        });
    }

    protected override Widget Build(BuildContext context) =>
        new Center(new SizedBox(width: 320, child: new Column(mainAxisSize: MainAxisSize.Min, spacing: 14, crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
            new Watch(() => new AdwProgressBar(_progress.Value)),
            new Watch(() => new Label(_status.Value, fontSize: 15) { Align = TextAlign.Center }),
            new Watch(() => new AdwButton(label: _busy.Value ? "Working…" : "Start", onPressed: _busy.Value ? null : Start) {
                Style = AdwButtonStyle.Suggested, Pill = true,
            }),
        ])));
}
```

Note `onPressed: _busy.Value ? null : Start`: a null callback disables the button.

## What is allowed where

| | From a background thread |
|---|---|
| Write a `Signal` | ✓ always |
| Mutate a widget directly | ✗ post it: `App.Post(() => _label.Text = …)` |
| An `Effect` that touches widgets | Only with `EffectAffinity.Deferred`, which runs it on the frame loop |

## Background, Latest and Slice

`Zigote.Core.Threading.Background` handles work nobody awaits. Failures are reported instead of silently dropped, a scope's work is cancelled when it is disposed, and results come back on the UI thread:

```csharp
var library = app.Background.Child("library");                     // the app's root scope → one per feature

library.Run(() => Decode(path), image => _cover.Image = image);    // work → result on the UI thread
var search = library.Latest();                                     // latest-wins slot
search.Run(ct => Filter(query, ct), Show, delay: TimeSpan.FromMilliseconds(120));   // debounced
library.Slice(list, 50_000, i => list.AddItem(Row(i)));            // N items per frame until done
```

`Slice` and `Deliver.WhenIdle` spread work across frames within a per-frame time budget; the app's frame loop drains `App.Background` for you. Four hundred results landing at once become a few frames of filling in, not one dropped frame. *Scheduling* has live examples of both.

## Blocs

For screens with real behaviour, `Zigote.Bloc` provides an ordered event pump with one immutable state record. `Restart()` gives latest-wins cancellation, so a newer search cancels an older one, and `Dispose()` cancels everything. Widgets call `bloc.Add(event)` and read `bloc.State.Value` inside a `Watch`.

```csharp
public sealed class SearchBloc(ISearchApi api) : Bloc<SearchEvent, SearchState>(SearchState.Empty)
{
    protected override async ValueTask OnEventAsync(SearchEvent e, CancellationToken ct)
    {
        if (e is SearchEvent.QueryChanged(var text))
        {
            Emit(Current with { Query = text, Busy = true });
            var token = Restart();                                  // cancels the previous search
            var hits = await api.SearchAsync(text, token);
            Emit(Current with { Hits = hits, Busy = false });
        }
    }
}
```
