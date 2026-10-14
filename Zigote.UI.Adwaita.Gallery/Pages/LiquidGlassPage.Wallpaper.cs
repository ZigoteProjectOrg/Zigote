using Zigote.UI.Svg;

namespace AdwaitaGallery.Pages;

public sealed partial class LiquidGlassPage
{
    private static readonly string[] WallpaperNames = ["Aurora", "Sunrise", "Midnight"];

    // Three bounded, process-lifetime documents, shared by the scenes and thumbnails.
    // SvgPicture releases each raster texture on unmount. No network or image decode is
    // needed to see the material, and the bright/dark folds make refraction easy to judge.
    private static readonly Lazy<SvgAsset>[] WallpaperAssets = [
        new(() => WallpaperAsset(
                dark: "#091e58",
                middle: "#307cbd",
                light: "#97eee3",
                ribbon: "#7468ed",
                edge: "#dae8ff"
            )
        ),
        new(() => WallpaperAsset(
                dark: "#401b58",
                middle: "#d55c86",
                light: "#ffcb99",
                ribbon: "#fb856b",
                edge: "#ffe8cf"
            )
        ),
        new(() => WallpaperAsset(
                dark: "#070e2b",
                middle: "#284b8d",
                light: "#69b8e1",
                ribbon: "#514db4",
                edge: "#b0c7ff"
            )
        ),
    ];

    private static SvgAsset WallpaperAsset(string dark, string middle, string light,
        string ribbon, string edge) => SvgAsset.FromString(
        $$"""
          <svg xmlns="http://www.w3.org/2000/svg" width="640" height="800" viewBox="0 0 640 800">
            <defs>
              <linearGradient id="sky" x1="0" y1="0" x2="1" y2="1">
                <stop stop-color="{{dark}}"/><stop offset=".55" stop-color="{{middle}}"/>
                <stop offset="1" stop-color="{{light}}"/>
              </linearGradient>
              <linearGradient id="fold" x1="0" y1="0" x2=".9" y2="1">
                <stop stop-color="{{light}}"/><stop offset=".45" stop-color="{{middle}}"/>
                <stop offset=".85" stop-color="{{dark}}"/><stop offset="1" stop-color="{{ribbon}}"/>
              </linearGradient>
              <linearGradient id="ribbon" x1="0" y1="0" x2="1" y2=".7">
                <stop stop-color="{{edge}}"/><stop offset=".18" stop-color="{{ribbon}}"/>
                <stop offset="1" stop-color="{{dark}}"/>
              </linearGradient>
            </defs>
            <path fill="url(#sky)" d="M0 0H640V800H0Z"/>
            <path fill="url(#fold)" d="M-180 850C-100 450 470 500 420-80L760-80V850Z"/>
            <path fill="url(#ribbon)" d="M-130 820C-60 480 430 670 690 170L760 800Z"/>
            <path fill="none" stroke="{{edge}}" stroke-opacity=".6" stroke-width="1.5"
                  d="M-130 820C-60 480 430 670 690 170"/>
          </svg>
          """
    );

    private static Widget Wallpaper(int index) => new SvgPicture(WallpaperAssets[index].Value);

    // This layer sits below LiquidGroup so it is included in the shared backdrop capture.
    // It deliberately ignores input: scrolls and taps still reach the content underneath.
    private sealed class MediaDimming(Func<float> opacity) : Widget
    {
        private Size _size;
        public override Size Measure(Constraints c) => _size = c.Constrain(Size.Zero);

        public override void Layout(Offset origin) =>
            Bounds = new Rect(
                x: origin.X,
                y: origin.Y,
                width: _size.Width,
                height: _size.Height
            );

        public override void Paint(PaintList paint) =>
            paint.AddRect(bounds: Bounds, color: Color.Black.WithAlpha(opacity()));

        public override Widget? HitTest(Offset point) => null;
    }
}
