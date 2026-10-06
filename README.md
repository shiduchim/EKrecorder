# EKrecorder

A Windows 11 screen recorder (C#, WinForms, .NET 10).

This repository holds the test build. **Step 1** (done) proved the monitor, capture and triangle architecture.
**Step 2** (done) added the video recording engine. **Step 3** (this build) adds reliable microphone and computer
audio.

## What Step 3 adds: audio that survives device changes

- Records the **microphone** and the **computer audio** (WASAPI loopback, shared mode, event-driven) and mixes them
  into the MP4 as one AAC track: 48 kHz stereo, 128 kbps. The microphone is mono, centred; computer audio is stereo.
  Mixing is in floating point, followed by a -1 dBFS peak limiter, so the mix never clips.
- **Devices.** Microphone: Windows' default *communications* microphone, or a selected one. Computer audio: Windows'
  default output *and* default communications output (captured once when they are the same device), or a selected
  one. No other device is captured, so virtual routing devices cannot double the sound.
- **A lost device never stops the recording.** Each input has its own state machine (Resolving → Running → Lost →
  Retrying → Running). While a device is missing, its part of the mix is silence. Windows' device notifications
  (IMMNotificationClient) are handed to a supervisor thread, which waits 500 ms for a burst of changes to settle and
  retries after 250 ms, 500 ms, 1 s, then every 2 s. It follows a new Windows default during the recording; a selected
  device that disappears is replaced by the default until it comes back. When switching, the old device keeps
  recording until the new one runs.
- **Timing.** Audio is placed by each packet's QPC timestamp on the same clock that paces the video, never by
  counting packets. Silence (a loopback device sends nothing while nothing plays) keeps its real length, and each
  device's clock drift is corrected continuously by a small resampler (at most ±0.5 % speed). The unit tests simulate
  two hours with ±150 ppm drift and timestamp jitter: the audio stays within 0.03 ms of true time.
- **Safety margins.** 500 ms WASAPI buffers, mixing 600 ms behind real time, no allocations in the capture path,
  capture and mix threads registered with MMCSS.
- **Health.** The window shows each input's level, state and devices. A microphone that sends nothing but exact digital
  silence for 8 seconds raises a warning (muted, or blocked by Windows privacy settings?); computer-audio silence is
  normal. The report lists every audio event with its recording time.
- Devices are open only while recording, or while "Show audio meters" is ticked and the window is not minimized.

## What Step 2 does

- Records the selected whole monitor to an MP4 file with Windows.Graphics.Capture.
- Default preset: at most 1920×1080, 15 fps, H.264 High profile, about 2 Mbps average and 6 Mbps peak (VBR), a
  keyframe every 2 seconds. The output size is fixed for the recording, keeps the monitor's aspect ratio and never
  upscales: a 4K monitor becomes 1920×1080, a 1366×768 monitor stays 1366×768, and a portrait (vertical) monitor
  gets at most 1080×1920.
- Paces the output at 15 fps on the QueryPerformanceCounter clock. When the screen has not changed, the previous
  picture is repeated ("duplicated frame"), so the file has a constant frame rate.
- Keeps frames on the GPU: the Direct3D 11 video processor scales and converts BGRA to NV12, and the frames go
  straight into Media Foundation's hardware H.264 encoder (NVIDIA, Intel or AMD).
- Checks that set-up with the first frame. If the encoder refuses it, EKrecorder tries the next set-up: CPU frames
  into the hardware encoder, then CPU frames into the software encoder. The report names the set-up in use, every
  fallback and why, and, for a refused frame, the sample, its GPU texture and the encoder's media types in full.
- Shows the blue corner triangle while recording. It is excluded from capture, so it is not in the video.
- Writes the file to `%LocalAppData%\EKrecorder\InProgress` while recording, then moves it to
  `Desktop\EKrecordings` with a timestamped name, for example `EKrecording 2026-10-06 14-03-22.mp4`.
- After each recording, opens a report: encoder used, output resolution, frame rate, file size, average and peak
  bitrate measured from the file, keyframes, dropped and duplicated frames, errors, CPU and memory use.

Not in this build, on purpose: hotkey, start with Windows, tray, the orange triangle, crash recovery, settings,
installer.

## Run it on your PC

1. Open the [Actions page](https://github.com/shiduchim/EKrecorder/actions) (sign in to GitHub first).
2. Click the newest **Windows build** run with a green check mark.
3. Scroll down to **Artifacts** and click **EKrecorder-win-x64**. A zip file downloads.
4. Right-click the zip, choose **Extract All**, and open the extracted folder.
5. Double-click **EKrecorder.exe**. If Windows says "Windows protected your PC", click **More info**, then
   **Run anyway** (the test build is not signed).
6. Pick a monitor and the audio devices (the Windows defaults are right for most tests), click **Start recording**,
   use the PC for 1–2 minutes, then click **Stop recording**.
7. The video is in Desktop > EKrecordings. The report opens in Notepad: paste its **SUMMARY** part into our chat.
   If the report has a **FAILURE DETAILS** part, paste the whole report. For audio tests, add the **AUDIO EVENTS**.

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
| `src/EKrecorder/Recording/GpuSamplePool.cs` | The encoder's GPU input textures (Media Foundation sample allocator) |
| `src/EKrecorder/Recording/H264Mp4Writer.cs` | Media Foundation H.264 → MP4, encoder choice and settings |
| `src/EKrecorder/Recording/MediaFoundationDiagnostics.cs` | Samples, textures and media types as text, for the report |
| `src/EKrecorder/Recording/RecordingReport.cs` | Moves the finished file, reads it back, writes the report |
| `src/EKrecorder/Audio/AudioCapture.cs` | The audio supervisor: devices, notifications, per-input state machines, retries |
| `src/EKrecorder/Audio/CaptureWorker.cs` | One WASAPI capture stream (microphone or loopback) on its own thread |
| `src/EKrecorder/Audio/TimelineWriter.cs` | Places a stream on the QPC timeline and corrects its clock drift |
| `src/EKrecorder/Audio/AudioTimeline.cs` | The timeline clock and each stream's buffer on it |
| `src/EKrecorder/Audio/AudioMixer.cs` | Sums the streams, limits, and feeds the AAC encoder |
| `tests/EKrecorder.Tests/` | Unit tests: hours of simulated drift, jitter, silence and lost devices |
