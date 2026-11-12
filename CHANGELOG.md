# Changelog

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
