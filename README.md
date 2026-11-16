# HyperSonic Remote for Unity Receiver

The Unity-side receiver for **HyperSonic Remote**, an iPhone app that turns your
phone into a low-latency touch pad for the Unity Editor — over Wi-Fi or USB,
with an optional live view of the game in your device's exact aspect ratio.

Unofficial tool. Not affiliated with Unity Technologies.

## Install (Package Manager, git URL)

1. In Unity, open **Window → Package Manager**
2. Click **+** → **Add package from git URL…**
3. Paste:

```
https://github.com/wabiplant/hypersonic-remote-for-unity-receiver.git
```

That's all. There is **no scene setup**: press Play in any scene and the
receiver starts automatically (it does not add objects to your Hierarchy).
If you prefer, you can also copy this folder into your project's `Packages/`
or `Assets/` directory.

**You install nothing else on your computer.** No companion app, no proxy,
no command-line tools, no terminal commands — for both Wi-Fi and USB.
Just add the package, press Play, and connect from the iPhone app.

## Requirements

- Unity 2022.3 or newer
- The HyperSonic Remote app on iPhone (App Store)
- Editor Play mode on macOS, Windows or Linux (the receiver runs inside the
  Editor; in player builds it stays inert unless you add the components
  yourself)
- For USB on Windows: iTunes or the Apple Devices app (they provide the
  Apple Mobile Device Service that every iPhone USB tool relies on).
  For USB on Linux: the `usbmuxd` daemon (part of libimobiledevice).
  macOS needs nothing — usbmuxd is built into the OS.

## How it works

- The app discovers the Editor automatically (Bonjour + broadcast) and sends
  touches over UDP (port 47777). One-way latency is about 4 ms on Wi-Fi and
  under 1 ms over USB. Discovery does not require Bonjour to be installed:
  the broadcast fallback (or entering the Editor machine's IP in the app)
  works everywhere.
- USB needs no setup either: a relay built into this package speaks the
  usbmux protocol directly and forwards touches from the cable to the
  receiver while you are in Play mode. Plug the cable in, choose USB in
  the app, done. (If you already run `iproxy 47778 47779` yourself, the
  built-in relay steps aside and yours is used.)
- The video view is a separate, optional stream (TCP port 47780) cropped to
  your device's aspect ratio, so what you see is exactly where you touched.
- Firewall prompts: allow Unity to accept local-network connections the
  first time (macOS: also allow "Local Network" in Privacy settings).

## Reading touches in your game

`Input.touches` can't be fed from C#, so read remote touches alongside your
normal input:

```csharp
foreach (var t in HyperSonicReceiver.Touches)
{
    // t.position (pixels), t.phase, t.deltaPosition, t.pressure, t.deltaTime
    // Same shape as UnityEngine.Touch.
}
```

Your existing input code keeps working unchanged — treat
`HyperSonicReceiver.Touches` as one more source of touches and merge it at
your input entry point. In the Editor you can also watch live touches in the
Game view by enabling `drawDebug` on the receiver (it is created at runtime;
find it via `FindFirstObjectByType<HyperSonicReceiver>()`).

## License

MIT — see [LICENSE](LICENSE).
