using EKrecorder.Audio;
using EKrecorder.Diagnostics;

namespace EKrecorder.Shell;

/// <summary>
/// The Settings window's live levels: the chosen microphone and computer audio are captured (levels only, nothing
/// is recorded) only while the window is on the screen. A new choice restarts the capture; hiding or closing the
/// window closes the devices. Used from the window thread only.
/// </summary>
internal sealed class AudioPreview : IDisposable
{
    private AudioCapture? _capture;
    private AudioSelection? _wanted;

    /// <summary>Closing captures, one after the other; a new capture starts only after the last one is closed.</summary>
    private Task _closing = Task.CompletedTask;

    /// <summary>Whether <paramref name="selection"/> is captured now (or will be as soon as the last one is closed).</summary>
    public bool IsShowing(AudioSelection selection) => _wanted == selection;

    /// <summary>Captures <paramref name="selection"/> (if it is not already being captured).</summary>
    public void Show(AudioSelection selection)
    {
        if (_wanted == selection)
        {
            return;
        }

        Close();
        _wanted = selection;
        if (_closing.IsCompleted)
        {
            Start(selection);
        }
        else
        {
            // The same devices are not opened again while they are still being closed.
            _closing.ContinueWith(
                _ =>
                {
                    if (_wanted == selection && _capture is null)
                    {
                        Start(selection);
                    }
                },
                TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    /// <summary>The levels since the last call (null while nothing is captured).</summary>
    public (AudioInputStatus Microphone, AudioInputStatus Computer)? Snapshot() => _capture?.Snapshot();

    /// <summary>Closes the devices. Closing can take a moment, so it never happens on the window thread.</summary>
    public void Close()
    {
        AudioCapture? capture = _capture;
        _capture = null;
        _wanted = null;
        if (capture is not null)
        {
            _closing = _closing.ContinueWith(
                _ =>
                {
                    try
                    {
                        capture.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Closing the audio preview failed", ex);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    public void Dispose() => Close();

    private void Start(AudioSelection selection)
    {
        try
        {
            var capture = new AudioCapture(selection, forRecording: false);
            capture.Start();
            _capture = capture;
        }
        catch (Exception ex)
        {
            Log.Error("The audio preview could not start", ex);
        }
    }
}
