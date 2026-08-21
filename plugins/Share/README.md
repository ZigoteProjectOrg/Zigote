# Share

Hand text, links and files to the platform's share sheet — the `share_plus` slot from the
[plugin roadmap](../../docs/plugin-roadmap.md).

```csharp
await SharePlugin.ShareTextAsync("Look at this https://zigote.dev", subject: "Trip");
ShareStatus status = await SharePlugin.ShareFilesAsync([shotPath], text: "My high score");
```

Never throws. `ShareStatus` is `Success` (handed over), `Dismissed` (the user closed the sheet)
or `Unavailable` (nothing to share, or no sheet here). Missing file paths are dropped; a call
with nothing left to share answers `Unavailable`. Static, so no `PluginHost.Register`.

| Platform | How | Status answers |
|---|---|---|
| Android | `ACTION_SEND` / `ACTION_SEND_MULTIPLE` through the system chooser; files are copied into `cacheDir/zigote-share` and served as `content://` URIs by a built-in provider | `Success` once the chooser starts — Android does not report the choice back without a receiver |
| iOS | `UIActivityViewController`, anchored at screen centre on iPad | `Success` / `Dismissed` |
| Desktop | no OS sheet worth reaching (WinRT `DataTransferManager`, `NSSharingServicePicker`, nothing on Linux) — the payload goes to the clipboard instead | `Unavailable` |

Android needs no manifest edit: the file provider is declared by attribute, with the authority
`${applicationId}.zigote.share`, and serves only the one cache directory the plugin writes to.
