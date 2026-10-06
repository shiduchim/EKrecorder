EKrecorder 1.0
==============

EKrecorder records one whole monitor with your microphone and the computer's sound,
to an MP4 file. It lives in the notification area (next to the clock).

Start / stop recording
  - Press the shortcut (Ctrl + Alt + R unless you changed it), or
  - right-click the EKrecorder icon and choose Start recording / Stop recording.

While recording
  - A tiny triangle sits in the bottom-right corner of the recorded monitor.
    It is not in the recording.
  - Blue: recording normally.
  - Orange: still recording, but something needs attention (for example the
    microphone is unplugged, the disk is almost full, or the monitor is gone).
    Hover over the EKrecorder icon to see what.
  - Windows will not go to sleep by itself while you record.

Where recordings go
  - While recording: %LocalAppData%\EKrecorder\InProgress (safe from OneDrive).
  - When you stop: your recordings folder, by default Desktop\EKrecordings.
  - Tray menu: Open recordings folder, Open last recording.

If the PC crashes or the power goes
  - The recording is written in a crash-safe way. At the next start EKrecorder
    finishes it and puts it in your recordings folder with "(recovered)" in the
    name. You lose only the last second or two.

Settings (double-click the EKrecorder icon)
  - Monitor, video quality (480p to 4K, 30 fps), recordings folder
  - Microphone, computer audio, audio quality
  - Start/stop shortcut, maximum recording time, Start EKrecorder with Windows

Problems
  - Logs: %LocalAppData%\EKrecorder\Logs
  - A report for every recording: %LocalAppData%\EKrecorder\Reports
  Send the log and the report of the recording that went wrong.
