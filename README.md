# EKrecorder

A Windows 11 screen recorder (C#, WinForms, .NET 10).

This repository holds the test build. **Step 1** (done) proved the monitor, capture and triangle architecture.
**Step 2** (this build) adds the video recording engine. Video only: no microphone or computer sound yet.

## What Step 2 does

- Records the selected whole monitor to an MP4 file with Windows.Graphics.Capture.
- Default preset: at most 1920×1080, 15 fps, H.264 High profile, about 2 Mbps average and 6 Mbps peak (VBR), a
  keyframe every 2 seconds. The output size is fixed for the recording, keeps the monitor's aspect ratio and never
  upscales: a 4K monitor becomes 1920×1080, a 1366×768 monitor stays 1366×768, and a portrait (vertical) monitor
  gets at most 1080×1920.
- Paces the output at 15 fps on the QueryPerformanceCounter clock. When the screen has not changed, the previous
  picture is repeated ("duplicated frame"), so the file has a constant frame rate.
- Keeps frames on the GPU: the Direct3D 11 video processor scales and converts BGRA to NV12, and the frames go
  straight into Media Foundation's hardware H.264 encoder (NVIDIA, Intel or AMD). The software encoder is used only
  when no hardware encoder is available; a CPU path is used only when the GPU cannot scale.
- Shows the blue corner triangle while recording. It is excluded from capture, so it is not in the video.
- Writes the file to `%LocalAppData%\EKrecorder\InProgress` while recording, then moves it to
  `Desktop\EKrecordings` with a timestamped name, for example `EKrecording 2026-10-06 14-03-22.mp4`.
- After each recording, opens a report: encoder used, output resolution, frame rate, file size, average and peak
  bitrate measured from the file, keyframes, dropped and duplicated frames, errors, CPU and memory use.

Not in this build, on purpose: microphone, computer audio, hotkey, start with Windows, tray, crash recovery, settings,
installer.

## Run it on your PC

1. Open the [Actions page](https://github.com/shiduchim/EKrecorder/actions) (sign in to GitHub first).
2. Click the newest **Windows build** run with a green check mark.
3. Scroll down to **Artifacts** and click **EKrecorder-win-x64**. A zip file downloads.
4. Right-click the zip, choose **Extract All**, and open the extracted folder.
5. Double-click **EKrecorder.exe**. If Windows says "Windows protected your PC", click **More info**, then
   **Run anyway** (the test build is not signed).
6. Pick a monitor, click **Start recording**, use the PC for 1–2 minutes, then click **Stop recording**.
7. The video is in Desktop > EKrecordings. The report opens in Notepad: paste its **SUMMARY** part into our chat.

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
| `src/EKrecorder/Capture/CaptureDevice.cs` | The Direct3D 11 device, created on the GPU that drives the monitor |
| `src/EKrecorder/Capture/CaptureSessionSetup.cs` | Capture permissions and session settings (border off, cursor, frame-rate cap) |
| `src/EKrecorder/Capture/CaptureTest.cs` | The Step 1 capture test |
| `src/EKrecorder/Recording/RecordingSession.cs` | One recording: capture, 15 fps pacing, conversion, encoding |
| `src/EKrecorder/Recording/GpuFrameConverter.cs` | GPU scaling and BGRA→NV12 with the Direct3D 11 video processor |
| `src/EKrecorder/Recording/CpuFrameConverter.cs` | CPU fallback for the same |
| `src/EKrecorder/Recording/H264Mp4Writer.cs` | Media Foundation H.264 → MP4, encoder choice and settings |
| `src/EKrecorder/Recording/RecordingReport.cs` | Moves the finished file, reads it back, writes the report |
