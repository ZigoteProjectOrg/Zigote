using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Theme;

/// <summary>
///     Monochrome line-icon glyphs from the bundled Material Icons face. Each constant is the
///     Private-Use codepoint for one icon; render it as text in the <see cref="Family" /> font
///     (via <see cref="Draw" />, the <c>Icon</c> widget, or
///     <c>
///         PaintList.AddText(..., fontFamily:
///         Icons.Family)
///     </c>
///     ). The face is registered under <see cref="Family" /> at app startup.
/// </summary>
public static class Icons
{
    // The full ~2,200-icon set (raw Material names) is generated into MaterialIcons.g.cs by
    // tools/IconGen.cs; the constants below are hand-picked, friendly-named aliases used across
    // the editor. Add new curated names here; reach for MaterialIcons.* for anything else.

    /// <summary>Font family name the Material Icons face is loaded under (see <c>App</c> startup).</summary>
    public const string Family = "MaterialIcons";

    // ── Transform tools ──
    public const string Move = "\ue89f"; // open_with
    public const string Rotate = "\ue41a"; // rotate_right
    public const string Scale = "\uf1ce"; // open_in_full
    public const string Pivot = "\ue3b4"; // center_focus_strong
    public const string Grid = "\ue3ec"; // grid_on
    public const string Snap = "\uf016"; // grid_4x4
    public const string Measure = "\ue41c"; // straighten

    // ── Transport ──
    public const string Play = "\ue037"; // play_arrow
    public const string Pause = "\ue034"; // pause
    public const string Stop = "\ue047"; // stop
    public const string StepForward = "\ue044"; // skip_next
    public const string StepBack = "\ue045"; // skip_previous

    // ── Edit actions ──
    public const string Add = "\ue145"; // add
    public const string AddBox = "\ue146"; // add_box
    public const string Delete = "\ue872"; // delete
    public const string Undo = "\ue166"; // undo
    public const string Redo = "\ue15a"; // redo
    public const string Copy = "\ue14d"; // content_copy
    public const string Save = "\ue161"; // save

    // ── Files / navigation ──
    public const string Folder = "\ue2c7"; // folder
    public const string FolderOpen = "\ue2c8"; // folder_open
    public const string Search = "\ue8b6"; // search
    public const string Filter = "\ue152"; // filter_list
    public const string FilterAlt = "\uef4f"; // filter_alt
    public const string ChevronDown = "\ue5cf"; // expand_more
    public const string ChevronRight = "\ue5cc"; // chevron_right
    public const string ChevronLeft = "\ue5cb"; // chevron_left
    public const string UnfoldLess = "\ue5d6"; // unfold_less (collapse)
    public const string UnfoldMore = "\ue5d7"; // unfold_more (expand)
    public const string Fullscreen = "\ue5d0"; // fullscreen (maximize)
    public const string FullscreenExit = "\ue5d1"; // fullscreen_exit (restore)
    public const string Close = "\ue5cd"; // close
    public const string MoreHoriz = "\ue5d3"; // more_horiz
    public const string MoreVert = "\ue5d4"; // more_vert
    public const string DropDown = "\ue5c5"; // arrow_drop_down
    public const string DropUp = "\ue5c7"; // arrow_drop_up
    public const string ArrowBack = "\ue5c4"; // arrow_back
    public const string ArrowForward = "\ue5c8"; // arrow_forward

    // ── Inspector / chrome ──
    public const string Tune = "\ue429"; // tune
    public const string Settings = "\ue8b8"; // settings
    public const string Layers = "\ue53b"; // layers
    public const string Dashboard = "\ue871"; // dashboard
    public const string Terminal = "\ueb8e"; // terminal
    public const string Tree = "\ue97a"; // account_tree
    public const string Category = "\ue574"; // category
    public const string Timeline = "\ue922"; // timeline
    public const string Palette = "\ue40a"; // palette
    public const string Colorize = "\ue3b8"; // colorize

    // ── Toggles ──
    public const string Visibility = "\ue8f4"; // visibility (eye)
    public const string VisibilityOff = "\ue8f5"; // visibility_off
    public const string Lock = "\ue897"; // lock
    public const string LockOpen = "\ue898"; // lock_open

    // ── Node kinds ──
    public const string Cube = "\ue9fe"; // view_in_ar mesh
    public const string Sun = "\ue430"; // wb_sunny light
    public const string LightMode = "\ue518"; // light_mode
    public const string Camera = "\ue04b"; // videocam
    public const string Bolt = "\uea0b"; // bolt script
    public const string Terrain = "\ue564"; // terrain
    public const string Water = "\ue798"; // water_drop

    // ── Indicators ──
    public const string Check = "\ue5ca"; // check
    public const string CheckBox = "\ue834"; // check_box
    public const string Circle = "\uef4a"; // circle
    public const string RadioChecked = "\ue837"; // radio_button_checked
    public const string Dot = "\ue061"; // fiber_manual_record
    public const string Info = "\ue88e"; // info
    public const string Warning = "\ue002"; // warning
    public const string Error = "\ue000"; // error
    public const string CheckCircle = "\ue86c"; // check_circle
    public const string Refresh = "\ue5d5"; // refresh
    public const string Cached = "\ue86a"; // cached
    public const string Sync = "\ue627"; // sync

    // ── Assets ──
    public const string File = "\ue24d"; // insert_drive_file
    public const string Audio = "\ue405"; // music_note
    public const string Image = "\ue3f4"; // image
    public const string Photo = "\ue410"; // photo
    public const string Code = "\ue86f"; // code
    public const string Map = "\ue55b"; // map

    // ── File browser ──
    public const string Home = "\ue88a"; // home
    public const string ArrowUpward = "\ue5d8"; // arrow_upward (up one directory)
    public const string Storage = "\ue1db"; // storage (volume/drive)
    public const string Computer = "\ue30a"; // computer
    public const string Download = "\ue2c4"; // file_download
    public const string Description = "\ue873"; // description (text document)
    public const string CreateNewFolder = "\ue2cc"; // create_new_folder
    public const string Movie = "\ue02c"; // movie (video files)

    /// <summary>
    ///     Paint <paramref name="glyph" /> centered inside <paramref name="box" /> at the given
    ///     pixel size. Material glyphs sit on the baseline within the em square, so the baseline
    ///     is placed to center the glyph vertically (a small offset compensates for design metrics).
    /// </summary>
    public static void Draw(PaintList paint, string glyph, Rect box, Color color, float size)
    {
        var x = box.X + (box.Width - size) * 0.5f;
        var baseline = box.Y + (box.Height + size) * 0.5f - size * 0.12f;
        paint.AddText(
            glyph,
            x,
            baseline,
            color,
            size,
            fontFamily: Family
        );
    }

    /// <summary>
    ///     Paint <paramref name="glyph" /> at an explicit baseline, left-aligned at
    ///     <paramref name="x" />.
    /// </summary>
    public static void DrawAt(PaintList paint, string glyph, float x, float baselineY, Color color,
        float size)
    {
        paint.AddText(
            glyph,
            x,
            baselineY,
            color,
            size,
            fontFamily: Family
        );
    }
}