// height: 220
class Demo : ComposedWidget
{
    private readonly Signal<int> _deleted = new(0);
    private AdwToastOverlay? _toasts;

    // Toasts float over the content, time out on their own, and can carry one action.
    protected override Widget Build(BuildContext context)
    {
        _toasts ??= new AdwToastOverlay(new Center(new Column(mainAxisSize: MainAxisSize.Min, spacing: 12, children: [
            new AdwButton(label: "Delete a file", onPressed: () =>
            {
                _deleted.Value++;
                _toasts!.AddToast(new AdwToast("File moved to the trash") { ButtonLabel = "Undo", OnButtonClicked = () => _deleted.Value-- });
            }) { Pill = true },
            new Watch(() => new Label($"{_deleted.Value} in the trash", fontSize: 13)),
        ])));
        return _toasts;
    }
}
