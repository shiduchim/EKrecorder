using EKrecorder.Audio;
using EKrecorder.Diagnostics;

namespace EKrecorder.Shell;

/// <summary>
/// The Settings window's live levels: the chosen microphone and computer audio are captured (levels only, nothing
/// is recorded) only while the window is on the screen. A new choice restarts the capture; hiding or closing the
/// window closes the devices.
/// </summary>
internal sealed class AudioPreview : IDisposable
{
    private AudioCapture? _capture;
    private AudioSelection? _selection;

    /// <summary>Captures <paramref name="selection"/> (if it is not already being captured).</summary>
    public void Show(AudioSelection selection)
    {
        if (_capture is not null && _selection == selection)
        {
            return;
        }

        Close();
        try
        {
            var capture = new AudioCapture(selection, forRecording: false);
            capture.Start();
            _capture = capture;
            _selection = selection;
        }
        catch (Exception ex)
        {
            Log.Error("The audio preview could not start", ex);
        }
    }

    /// <summary>The levels since the last call (null while nothing is captured).</summary>
    public (AudioInputStatus Microphone, AudioInputStatus Computer)? Snapshot() => _capture?.Snapshot();

    /// <summary>Closes the devices. Closing can take a moment, so it never happens on the window thread.</summary>
    public void Close()
    {
        AudioCapture? capture = _capture;
        _capture = null;
        _selection = null;
        if (capture is not null)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    capture.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Error("Closing the audio preview failed", ex);
                }
            });
        }
    }

    public void Dispose() => Close();
}
