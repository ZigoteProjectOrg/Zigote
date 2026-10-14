using Zigote.Core.Engine;
using Zigote.Videoplayer;

namespace AdwaitaGallery.Pages;

/// <summary>
///     Video Player — <see cref="VideoPlayerView" /> over a sample clip: on-video controls,
///     gestures, keyboard, fullscreen and picture-in-picture.
/// </summary>
public sealed class VideoPlayerPage : ComposedWidget
{
    private const string SampleUrl =
        "https://test-videos.co.uk/vids/bigbuckbunny/mp4/h264/720/Big_Buck_Bunny_720_10s_1MB.mp4";

    // One player for the whole run, not one per visit: picture-in-picture outlives the page that
    // opened it, so the page leaving cannot be what ends playback.
    private static VideoPlayer? _player;

    protected override Widget Build(BuildContext context)
    {
        var player = Player();
        return new GalleryPage(
            title: "Video Player",
            description: "A media viewer's player: controls over the picture that get out of the way.",
            iconName: MaterialIcons.SmartDisplay
        ) {
            Children = {
                FFmpeg.IsAvailable()
                    ? new ClipRRect(radius: 12f, child: new VideoPlayerView(player) { AltText = "Sample video" })
                    : new AdwStatusPage { Title = "ffmpeg Not Found", Description = "Install ffmpeg to play video." },
                Demo.Group(
                    title: "Using It",
                    description: "Click plays and pauses, double-click goes fullscreen, press and hold plays at 2×. " +
                                 "Space, ← →, J L, ↑ ↓, M, F and P work once the picture has focus.",
                    new AdwButtonRow(title: "Open a Video…", onPressed: () => _ = OpenFile(player)),
                    new AdwButtonRow(
                        title: "Picture in Picture",
                        onPressed: () => PictureInPicture.Open(player: player, from: Owner)
                    )
                ),
            },
        };
    }

    private static VideoPlayer Player()
    {
        if (_player is not null) return _player;
        _player = new VideoPlayer(App.Active?.Engine.Audio);
        string source = Environment.GetEnvironmentVariable("ZIGOTE_SAMPLE_VIDEO") ?? SampleUrl;
        if (FFmpeg.IsAvailable()) _ = Open(player: _player, source: source);
        return _player;
    }

    private static async Task OpenFile(VideoPlayer player)
    {
        string? path = await FileDialog.OpenFileAsync(
            title: "Open a Video",
            filters: [
                new FileDialogFilter(name: "Videos", extensions: ["mp4", "mkv", "webm", "mov", "avi", "m4v"]),
                new FileDialogFilter(name: "All files", extensions: ["*"]),
            ]
        );
        if (path is not null) await Open(player: player, source: path);
    }

    private static async Task Open(VideoPlayer player, string source)
    {
        try
        {
            await player.OpenAsync(source: source, maxHeight: 720);
        }
        catch (Exception)
        {
            // Surfaced by the player's Error signal, which the controls show.
        }
    }
}
