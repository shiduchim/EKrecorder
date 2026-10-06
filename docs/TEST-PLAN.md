# EKrecorder 1.0 test plan

**Automatic** = checked on every build by GitHub Actions (a Windows machine without a GPU, sound devices or a real
monitor). **On the PC** = needs the real machine. After each test, the recording's report is in
`%LocalAppData%\EKrecorder\Reports` (tray → Open recordings folder for the video).

## Video

| Check | Automatic | On the PC |
|---|---|---|
| Monitor 1 and Monitor 2 | records the build machine's monitor | Settings → pick each monitor, record 30 s each |
| 1080p and 4K → 1080p scaling | output size rule (unit tests), 4K software encode | 1080p step on Monitor 2 (4K): file is 1920×1080 |
| 1440p (default) and 4K | 4K encode, A/V timing at 4K | 1440p and 4K steps on Monitor 2 |
| 30 fps | frame count / duration of every test recording | report: "Frame rate 30 fps", dropped frames 0 |
| Scrolling, Revit text, menus, mouse | – | scroll a Revit sheet and a long PDF; text sharp when paused |
| Hardware encoder (NVIDIA) | – (no GPU) | report: "NVIDIA hardware H.264 encoder", GPU path |
| Software fallback | self-test forces a refused first set-up | – |

## Audio

| Check | Automatic | On the PC |
|---|---|---|
| Microphone + computer audio | silent tracks, same length as video | a Zoom/Teams call: both voices in the file |
| A/V sync | beep vs white frame, ≤ 2 ms, Windows decoders and FFmpeg | clap on camera / in a call; long recording below |
| Mic unplug / replug | unit tests of the timeline | unplug the headset for 20 s mid-recording: triangle orange, then blue; silence in the gap |
| Change the Windows default mic | – | change it in Sound settings while recording |
| Change the playback device | – | switch speakers ↔ headset while recording |
| Bluetooth disconnect / reconnect | – | if available |
| Mic hardware mute | – | mute for 15 s: triangle orange after about 8 s |
| Computer-audio silence | – | nothing playing: triangle stays blue |
| Long recording sync | 2-hour drift simulations (unit tests) | 1 hour or more: speech still matches lips/clicks at the end |

## Reliability

| Check | Automatic | On the PC |
|---|---|---|
| 1 hour; several hours | – | record 1 h, then 3–4 h (set Stop recording after: Off) |
| Start/stop repeatedly | self-test records three times in a row | press the shortcut 10 times, 5 s apart |
| Lock Windows | – | Win + L for a minute while recording |
| Monitor reconnect | – | switch the recorded monitor off/on (or unplug it) while recording: triangle orange on the main monitor, then back |
| Low disk | thresholds (unit tests) | optional: record to a nearly full USB stick folder |
| App force-closed | killed while recording, recovered at the next start | Task Manager → End task while recording, start EKrecorder again |
| Power loss / crash | file cut at hundreds of points (unit tests) | optional: hold the power button while recording |
| Recordings folder unavailable | – | choose a USB stick folder, unplug it, record: saved in Desktop\EKrecordings |
| Windows shutdown | – | shut down while recording: file saved at the next start |
| Sleep | – | sleep while recording: saved on wake-up |

## UI

| Check | Automatic | On the PC |
|---|---|---|
| Global shortcut starts/stops | registration and conflict (self-test) | default Ctrl + Alt + R, then your own (e.g. Shift + Up) |
| Conflicting shortcut message | self-test | pick a shortcut another program uses: clear message |
| Start with Windows | Run value set by the installed app | restart Windows: EKrecorder in the tray, no window |
| Tray menu | – | each item |
| Settings remembered | save/load, damaged file (unit + self-test) | change everything, Exit, start again |
| No microphone held open while idle | devices open only in a recording | Windows privacy indicator (mic icon) off while idle |
| No test/debug text | – | look at Settings and the tray menu |
| Installer | silent install, start, single copy, exit, uninstall | install, use, uninstall: recordings stay |
