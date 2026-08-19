# FilePicker

File and folder picking for Zigote — the `image_picker` + `file_picker` slot from the
[plugin roadmap](../../docs/plugin-roadmap.md), as one package.

```csharp
string? file  = await FilePickerPlugin.OpenFileAsync("Choose a song", [("Audio", ["mp3", "flac"])]);
string[] many = await FilePickerPlugin.OpenFilesAsync();
string? dir   = await FilePickerPlugin.PickFolderAsync("Music folder");
string? save  = await FilePickerPlugin.SaveFileAsync("Export", suggestedName: "playlist.m3u");
```

Null (or an empty array) means the user cancelled. On-demand calls, so no
`PluginHost.Register`. Call from the UI thread (desktop dialogs are UI-thread-only, enforced by
`FileDialog`); await anywhere.

| Platform | Backend | Results |
|---|---|---|
| Desktop | `Zigote.Core.Engine.FileDialog` — portal/zenity, IFileDialog, NSOpenPanel, plus the in-app fallback | filesystem paths |
| Android | Storage Access Framework via a throwaway transparent activity | files: `content://` URI strings — read via ContentResolver, not File IO; folder: a real path on primary storage, null on SD/cloud trees |
| iOS | not yet — UIDocumentPickerViewController is future work | |

Android notes: filters are desktop-only (SAF filters by MIME; the picker shows `*/*`); one
picker at a time — a second concurrent call answers as cancelled immediately.
