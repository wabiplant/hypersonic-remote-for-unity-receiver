# Changelog

## 1.0.3

- Correct the 1.0.2 stale-object sweep: leftovers float outside any scene
  (Scene.IsValid() is false for them), so the first sweep skipped them.
  Assets are now excluded via AssetDatabase path instead, and the sweep
  actually removes the accumulated objects.

## 1.0.2

- Fix auto-spawn accumulating one leftover receiver/streamer object per Play
  session in the Editor (the hidden auto-spawned objects survive Play mode
  and were invisible to the previous existence check). Stale auto-spawned
  objects are now destroyed and recreated fresh each Play session.
  Manually placed receivers are still respected.

## 1.0.1

- Ship .meta files for all package assets (required for immutable git
  packages; without them Unity ignores the scripts).

## 1.0.0

- First release.
- Touch receiver (Wi-Fi UDP / USB via usbmux) with automatic discovery
  (Bonjour + broadcast) and one-way latency readout.
- Optional live game view streamed in the device's aspect ratio, with
  Lightest / Standard / High presets.
- Zero scene setup: the receiver auto-starts when Play begins.
- Display/touch consistency: touches are mapped into the visible (cropped)
  area, so the point you see is the point you touch.
