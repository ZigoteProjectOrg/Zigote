# Haptics

Haptic feedback — the `vibration` slot from the [plugin roadmap](../../docs/plugin-roadmap.md).

```csharp
HapticsPlugin.Play(Haptic.Selection);              // picker row moved under the finger
HapticsPlugin.Play(Haptic.Success);                // operation finished
HapticsPlugin.Vibrate(TimeSpan.FromMilliseconds(200), amplitude: 0.6); // a game effect
if (!HapticsPlugin.Supported) { /* desktop — nothing to feel */ }
```

Every call answers false where there is nothing to feel: all desktops, hardware without a
vibrator, and Android without the `VIBRATE` permission in the manifest. Static, so no
`PluginHost.Register`.

| Platform | How |
|---|---|
| Android | `Vibrator` (via `VibratorManager` on API 31+) driven with `VibrationEffect` waveforms; the patterns live in `HapticsPlugin.PatternFor` |
| iOS | `UISelectionFeedbackGenerator` / `UIImpactFeedbackGenerator` / `UINotificationFeedbackGenerator` |
| Desktop | no-op, `false` |

`Vibrate` has no iOS equivalent — iOS plays one heavy impact instead, and durations are clamped
to five seconds.
