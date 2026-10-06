# EKrecorder

A Windows 11 screen recorder (C#, WinForms, .NET 10).

This repository holds **Step 1: the capture spike**, an engineering test build that checks the riskiest Windows
assumptions before the real recorder is built. It records nothing to video yet.

## What the spike checks

- Finds every connected monitor and numbers them left to right: Monitor 1, Monitor 2, ...
- **Identify** shows EKrecorder's number on each screen for 3 seconds.
- Captures one whole monitor with Windows.Graphics.Capture for about 10 seconds (no region capture).
- Tries to turn the yellow capture border off, and records whether Windows allowed it.
- Shows the recording indicator: a tiny blue triangle (12 logical pixels, DPI aware) in the exact bottom-right
  corner of the recorded monitor. It is a small native layered window: borderless, click-through, always on top,
  never takes focus, not in Alt+Tab or the taskbar.
- Excludes the triangle and the Identify numbers from capture with `WDA_EXCLUDEFROMCAPTURE`, applied and read back
  **before** the window is shown. If that fails, the window is never shown.
- Checks the captured frames: the triangle and the Identify numbers must not be in them.
- Writes `report.txt` (PASS/FAIL per check), `log.txt` (every Windows API call and decision) and pictures.

Not in this build, on purpose: microphone, computer audio, MP4 encoding, hotkeys, start with Windows, tray,
crash recovery, settings, installer.

## Run the test on your PC

1. Open the [Actions page](https://github.com/shiduchim/EKrecorder/actions) (sign in to GitHub first).
2. Click the newest **Windows build** run with a green check mark.
3. Scroll down to **Artifacts** and click **EKrecorder-spike-win-x64**. A zip file downloads.
4. Right-click the zip, choose **Extract All**, and open the extracted folder.
5. Double-click **EKrecorder.exe**. If Windows says "Windows protected your PC", click **More info**, then
   **Run anyway** (the test build is not signed).
6. Pick Monitor 1 or Monitor 2 (click **Identify** if unsure), then click **Run 10-second test**.
7. Watch the bottom-right corner of that monitor and its edges. Answer the two Yes/No questions.
8. A results folder opens (Desktop > EKrecorder test results). Paste `report.txt` into our chat.

During the test a small square blinks pink and green next to the triangle. It is a test marker that is deliberately
**not** hidden from capture, so the test can prove it really sees that corner of the screen.

## Build it yourself

Visual Studio 2026 with the **.NET desktop development** workload (it includes the .NET 10 SDK).
Open `EKrecorder.sln` and press F5. From a terminal: `dotnet build EKrecorder.sln`.

The workflow in `.github/workflows/build.yml` builds on Windows, publishes one self-contained `EKrecorder.exe`
(no .NET install needed), runs a self-test on the build machine, and uploads the download.

## Where things are

| File | What it does |
|---|---|
| `src/EKrecorder/Program.cs` | Start-up: PerMonitorV2 DPI awareness, logging, `--selftest` mode |
| `src/EKrecorder/SpikeForm.cs` | The small test window |
| `src/EKrecorder/Monitors/` | Monitor detection, left-to-right numbering, stable monitor ids |
| `src/EKrecorder/Overlays/OverlayWindow.cs` | The one native layered window used for every marker, with the capture exclusion |
| `src/EKrecorder/Overlays/RecordingIndicator.cs` | The blue corner triangle |
| `src/EKrecorder/Overlays/IdentifyOverlays.cs` | The Identify numbers |
| `src/EKrecorder/Capture/CaptureTest.cs` | The 10-second test, the frame checks and the report |
| `src/EKrecorder/Capture/CaptureDevice.cs` | The Direct3D 11 device, created on the GPU that drives the monitor |
| `src/EKrecorder/Capture/CaptureItemFactory.cs` | Whole-monitor capture item through `IGraphicsCaptureItemInterop` |
