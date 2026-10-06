# EKrecorder

A simple, reliable Windows 11 recorder for calls and screen work. It records one whole monitor at 30 fps with the
microphone and the computer's sound into an MP4, and lives quietly in the notification area.

## Install

Download **EKrecorder-Setup** from the latest
[Windows build](https://github.com/shiduchim/EKrecorder/actions/workflows/build.yml) run and open
`EKrecorder-Setup-1.0.0.exe`. It installs for your Windows user only (no administrator rights), adds a Start menu entry,
and can start EKrecorder right away. Remove it under Settings → Apps → Installed apps; your recordings are never
touched.

For testing without installing, **EKrecorder-win-x64** holds the same single `EKrecorder.exe`.

## Use

| | |
|---|---|
| Start / stop | the shortcut (default **Ctrl + Alt + R**), or right-click the tray icon |
| While recording | a tiny triangle in the recorded monitor's bottom-right corner (not in the recording): **blue** = fine, **orange** = still recording but needs attention (hover over the tray icon to see why) |
| Settings | double-click the tray icon |
| Recordings | `Desktop\EKrecordings` by default; tray menu → Open recordings folder / Open last recording |

Settings: monitor (with Identify), video quality (480p · 720p · 1080p · **1440p** · 4K, all 30 fps), recordings folder,
microphone and computer audio (Windows defaults or a specific device), audio quality (96 · **128** · 160 · 192 ·
256 kbps), start/stop shortcut, maximum recording time, start with Windows. Bold = default.

## How it keeps recordings safe

- **Crash-safe files.** While recording, Windows writes a fragmented MP4 about every 0.3 s to
  `%LocalAppData%\EKrecorder\InProgress` (never straight into the Desktop or a synced folder). After stopping it is
  turned into a regular MP4 in place (no copying, no re-encoding), checked, and only then moved to the recordings
  folder. If the PC crashes, the power fails or EKrecorder is killed, the next start recovers the recording up to its
  last second or two.
- **Audio that survives device changes.** Microphone and computer audio are captured with WASAPI; a device that
  disappears is filled with silence and reconnected automatically, a chosen device falls back to the Windows default
  and returns when it is back, and the timeline follows one clock so sound stays in sync for hours.
- **The monitor can come and go.** If the recorded monitor disconnects or sleeps, recording continues (the picture
  holds) and the same physical monitor is picked up again when it returns.
- **Disk space.** The triangle turns orange when space runs low; the recording is stopped and saved while there is
  still room.
- **Windows events.** No automatic sleep while recording; a recording is stopped and saved when Windows shuts down,
  logs off or goes to sleep.

Logs: `%LocalAppData%\EKrecorder\Logs` (size-limited). A technical report for every recording:
`%LocalAppData%\EKrecorder\Reports`. Nothing opens by itself.

## Engine

Windows.Graphics.Capture → Direct3D 11 video processor (scale, NV12) → Media Foundation H.264 (NVIDIA / Intel / AMD
hardware encoder first; CPU frames into the hardware encoder next; the software encoder last) → MP4. AAC-LC 48 kHz
stereo from a float mix of the microphone (mono, centred) and the computer audio, with drift correction and a
limiter. No FFmpeg, no DirectShow.

## Build

Requires the .NET 10 SDK. `dotnet build EKrecorder.sln` builds on Windows (and compiles on Linux/macOS for quick
checks); `dotnet test tests/EKrecorder.Tests` runs the unit tests on any OS. The GitHub Actions workflow builds Debug
and Release with warnings as errors, runs the unit tests, the self-test on a real Windows (recordings, recovery after
a kill, audio/video timing, shortcut), builds the installer (`installer/EKrecorder.iss`, Inno Setup) and uploads:

- `EKrecorder-win-x64`: `EKrecorder.exe` (single file, self-contained) and `README.txt`
- `EKrecorder-Setup`: the installer
- `test-results`: the self-test reports, recordings and screenshots

Code signing can be added later: sign `EKrecorder.exe` after publishing and set `SignTool` in the installer script;
nothing else changes.

See [docs/TEST-PLAN.md](docs/TEST-PLAN.md) for what is tested automatically and what to check on a real PC.
