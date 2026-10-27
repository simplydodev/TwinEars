# TwinEars

Play your Windows audio on two (or more) devices at once: two pairs of Bluetooth earbuds, or Bluetooth + wired headphones.

It's a single portable `TwinEars.exe` (under 100 KB). There's no installer, no drivers, no admin rights and no registry changes. It runs on Windows 10/11 out of the box, because the .NET Framework 4.8 it uses is part of Windows.

## Download

Get `TwinEars.exe` from this repository's **Releases** page, and only from there: copies hosted elsewhere could have been tampered with. Each release is built automatically by GitHub Actions from the source code in this repository, and lists the file's SHA-256 checksum. To check your download matches, run this in PowerShell and compare:

```powershell
Get-FileHash .\TwinEars.exe -Algorithm SHA256
```

The exe isn't code-signed, so Windows may say *"Windows protected your PC"* the first time. Click **More info > Run anyway**, or build it yourself from source (see below) to avoid the warning entirely.

## How to use it

1. Connect both devices to the laptop.
2. Put `TwinEars.exe` in any folder and run it.
3. **Main output**: leave it on *Windows default output*. Windows keeps playing there exactly as before.
4. **Also play on**: tick the other device.
5. Click **Mirroring: OFF** so it switches to **ON**. Click it again (or use the tray icon) to turn it off.

You can tick the main device too. It's skipped while it's the main output and gets the copy if you later switch the Windows default to the other device.

### Getting both in sync

Bluetooth adds lag (typically 100–250 ms), and the copy can only be *delayed*, never sped up. So:

- **Make the laggier device the main output.** That's usually the Bluetooth one when the other is wired.
- Select the copy device in the list, click **Play test clicks**, and raise **Delay** until the clicks line up.
- With two Bluetooth devices, try both ways round and keep whichever needs a positive delay.

Delay and volume are saved per device.

## Why it can't break your audio setup

- **It only listens to the main output.** It uses Windows' built-in loopback capture, which is read-only. Drivers, Dolby Atmos, spatial sound, enhancements, EQ, sample rates and the default device are never touched.
- **The copy is an ordinary app stream**, like a music player's. It appears in the volume mixer as *TwinEars*, and Windows is told not to remember a volume for it.
- **Off really means off.** Turning mirroring off, exiting, or even killing the process closes every stream, and there's nothing to undo or restore.
- **It never copies a device onto itself**, so there are no echo loops. Only one copy of TwinEars can run at a time.
- **When nothing plays for 20 seconds,** the copy's stream is closed, so the earbuds and the laptop can sleep normally.
- **Devices can come and go.** If an earbud disconnects or changes mode, its status shows the reason and it resumes by itself when it's back.

## Privacy

- TwinEars has no network code at all: it never connects to the internet and sends nothing anywhere.
- The audio it copies passes through memory on its way to your other device. It's never recorded or saved, and the microphone isn't used.
- The only files it writes are its own settings file, plus a log or self-test report if you ask for one (see below).

About Dolby: the main device is unaffected either way. The copy is whatever Windows mixes for the main device. Whether that includes Dolby's processing depends on how Dolby is installed on your laptop. The copy device also gets its own Dolby or spatial sound if you've enabled it on that device.

## Settings and files

- Settings are saved in `TwinEars.ini` next to the exe, as plain text. Delete it to reset.
- If that folder is read-only, the settings go to `%LOCALAPPDATA%\TwinEars\` instead.
- To remove TwinEars, delete the folder.

Optional checkboxes in the window:
- **Turn mirroring on automatically when TwinEars starts**
- **Keep running in the system tray when this window is closed**

## Command line (optional)

| Command | What it does |
|---|---|
| `TwinEars.exe /toggle` | Turns mirroring on/off in the copy that's already running (or starts it, ON) |
| `TwinEars.exe /on`, `/off` | Turns mirroring on / off |
| `TwinEars.exe /exit` | Closes the running copy |
| `TwinEars.exe /tray` | Starts hidden in the system tray |
| `TwinEars.exe /log` | Writes `TwinEars.log` for troubleshooting |
| `TwinEars.exe /selftest` | Silent diagnostic run (all copies at volume 0). Writes `TwinEars-selftest.txt` next to the exe |

- **Keyboard shortcut:** make a desktop shortcut to `TwinEars.exe /toggle`, open its Properties, and set a *Shortcut key* (e.g. Ctrl+Alt+D).
- **Start with Windows:** put a shortcut to `TwinEars.exe /tray` in `shell:startup`. This is optional and nothing does it for you.

## Troubleshooting

| What you see | What to do |
|---|---|
| Yellow banner: *Windows won't play anything on … (busy)* | Windows is refusing all ordinary audio on the main output, for every app, not just TwinEars. If exclusive mode is allowed, an app (some music players, games or audio tools) has probably locked it: close that app, or untick "Allow applications to take exclusive control" (Sound control panel `mmsys.cpl` > Playback > device > Properties > Advanced). If exclusive mode is already off, the device or driver itself is stuck: for a headphone jack, check something is plugged in, or unplug and replug it; otherwise restart. Meanwhile pick a different Main output. |
| *Problem: Windows reports this device as busy* | Same thing, but on the copy device. |
| The copy plays later than the main device | Swap them: make the copy device the main output. |
| Bluetooth sound turns mono or tinny | An app opened the earbuds' microphone, which switches them to "Hands-Free" mode. Close calls, or set a different microphone as the default. |
| Stutter with two Bluetooth devices | They share one Bluetooth radio. Move closer, or reconnect them. If your laptop and Wi-Fi support it, use 5 GHz Wi-Fi. If it persists, raise `BufferMs` / `SafetyMs` in `TwinEars.ini`. |
| *Not connected (resumes when it's back)* | The device is off or out of range. It starts again on its own. |

If something else looks wrong, run `TwinEars.exe /selftest` and look at `TwinEars-selftest.txt`.

## Building from source

Double-click `build.cmd`, which uses the C# compiler built into Windows (nothing to install). It produces `dist\TwinEars.exe`. The code is plain C# 5 (the language version that compiler supports) with no third-party libraries.

```
src\Wasapi.cs         Windows Core Audio interop (shared-mode streams only)
src\MirrorWorker.cs   the audio engine: loopback capture -> delay/drift control -> output
src\MirrorManager.cs  starts/stops/retries one worker per copy device
src\MainForm.cs       window and tray icon
src\Settings.cs       portable INI settings + optional log
src\SelfTest.cs       /selftest diagnostics
src\Assets.cs         icon and test-click sound, generated in code
src\Program.cs        entry point, single instance, command forwarding
src\app.manifest      runs as a normal user, Windows 10/11, DPI aware
src\TwinEars.ico     the exe icon (regenerate with: TwinEars.exe /make-icon src\TwinEars.ico)
.github\workflows\    builds every push; publishes a release for every v* tag
```

**Publishing a release:** push a version tag, for example `git tag v1.0.0` then `git push origin v1.0.0`. GitHub Actions builds the exe and creates the release with the file and its checksum attached.

How the engine stays in sync: each copy keeps the total queued audio at *buffer + safety margin + your delay*. Bluetooth and wired devices run on slightly different clocks, so the engine corrects the drift by nudging the copy's playback speed by at most 0.5% (inaudible). It uses Windows' own resampler where available, and otherwise drops or repeats single samples.

## License

MIT; see [LICENSE](LICENSE). The software is provided as is, without warranty.

Windows is a trademark of Microsoft. Dolby and Dolby Atmos are trademarks of Dolby Laboratories. Bluetooth is a trademark of Bluetooth SIG. Realtek is a trademark of Realtek Semiconductor. TwinEars is an independent project and isn't affiliated with or endorsed by any of them.
