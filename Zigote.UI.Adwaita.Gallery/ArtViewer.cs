namespace AdwaitaGallery;

/// <summary>
///     The full-size picture in an <see cref="InteractiveViewer" />: drag to pan, pinch or
///     ⌘/Ctrl-scroll to zoom about the pointer, double-click to toggle between fit and 3×.
/// </summary>
/// <remarks>
///     Opened from a grid tile or the carousel's expand button, and reloaded at four times the
///     thumbnail's resolution — zoom can only show what was decoded, so the deep zoom needs a decode
///     that deserves it. The bytes are the same ones the thumbnail already put on disk, so the
///     re-decode costs a disk read rather than a round trip.
/// </remarks>
internal static class ArtViewer
{
    // 2048 on the long edge is 16 MB of GPU for one picture, which is fine for the one dialog at a
    // time this opens, and holds up to roughly 8× zoom before the pixels show.
    private const uint FullMaxDim = 2048;

    public static void Show(ArtPiece piece)
    {
        var zoom = new Signal<float>(1f);
        var viewer = new InteractiveViewer(new ArtImage(piece, FullMaxDim, false)) {
            MaxScale = 10f,
            DoubleTapScale = 3f,
            OnScaleChanged = scale => zoom.Value = scale,
        };

        // The hint sits below the viewer rather than floating over it: an overlay would answer the
        // pointer where it lay, and a strip of dead surface in the middle of a pannable picture is
        // exactly the sort of thing nobody can name but everybody feels.
        var content = new Column(crossAxisAlignment: CrossAxisAlignment.Stretch) {
            Children = {
                new Expanded(viewer),
                new Padding(
                    EdgeInsets.Only(Spacing.Lg, 0f, Spacing.Lg, Spacing.Md),
                    Demo.Bar(
                        new Watch(() => Demo.Value($"{zoom.Value:0.0}×")),
                        Demo.Caption(
                            "Drag to pan · Pinch or ⌘-scroll to zoom · Double-click to toggle"
                        )
                    )
                ),
            },
        };

        Demo.ShowDialog(
            $"Art by {piece.Artist}",
            content,
            920f,
            720f,
            headerStart: Demo.IconButton(MaterialIcons.ZoomOutMap, () => viewer.Reset())
        );
    }
}
