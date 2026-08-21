# AppSettings

Open the OS settings page for this app — the `app_settings` slot from the
[plugin roadmap](../../docs/plugin-roadmap.md). The other half of a permanently denied
permission: the app cannot ask again, so it offers a button that opens Settings.

```csharp
if (!await PermissionsPlugin.RequestAsync(...))
    await AppSettingsPlugin.OpenAsync();                       // this app's page
await AppSettingsPlugin.OpenAsync(SettingsPage.Notifications);
```

False means there is no such page here. Static, so no `PluginHost.Register`.

| Platform | App | Notifications | Location |
|---|---|---|---|
| Android | `ACTION_APPLICATION_DETAILS_SETTINGS` | `ACTION_APP_NOTIFICATION_SETTINGS` | `ACTION_LOCATION_SOURCE_SETTINGS` |
| iOS | `openSettingsURLString` | notification settings on iOS 16+, else the app page | the app page (iOS links nothing else) |
| Desktop | `false` — desktops have no per-app settings page worth guessing at | | |
