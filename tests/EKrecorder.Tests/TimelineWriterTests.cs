using EKrecorder.Audio;
using Xunit.Abstractions;

namespace EKrecorder.Tests;

/// <summary>
/// The audio timeline under the conditions of a long Zoom or Teams call: device clocks that drift against the QPC
/// clock, jittery packet timestamps, a loopback stream that goes quiet, a device that is lost and comes back with a
/// different clock, and packets that jump.
/// </summary>
public sealed class TimelineWriterTests
{
    private const int Rate = TimelineClock.SampleRate;
    private readonly ITestOutputHelper _output;

    public TimelineWriterTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void PerfectDeviceIsPlacedExactly()
    {
        var ring = new TimelineRing();
        var writer = new TimelineWriter(ring, Rate, 1);
        var device = new SimulatedDevice(driftPpm: 0, jitterMs: 0, channels: 1, seed: 1);
        var check = new TimelineCheck(1, 1, ring);
        device.StartAt(0.5);
        while (device.Now < 60)
        {
            check.ReadUntil(device.CapturePacket(writer), p => p < (0.5 * Rate) ? Expect.Silence : Expect.Signal);
        }

        Report(check, writer, "perfect device, 60 s");
        Assert.True(check.MaxErrorMs < 0.001, $"max error {check.MaxErrorMs} ms");
        Assert.Equal(0, check.Holes);
        Assert.Equal(0, check.NoiseInSilence);
        Assert.Equal(0, writer.Resyncs);
    }

    [Theory]
    [InlineData(150, 0.3, 1, 2.0)]
    [InlineData(-150, 0.3, 2, 2.0)]
    [InlineData(400, 0.5, 1, 0.5)]
    [InlineData(-400, 0.5, 2, 0.5)]
    public void StaysWithinAMillisecondForHours(double driftPpm, double jitterMs, int channels, double hours)
    {
        const double start = 0.25;
        var ring = new TimelineRing();
        var writer = new TimelineWriter(ring, Rate, channels);
        var device = new SimulatedDevice(driftPpm, jitterMs, channels, seed: 7);
        var check = new TimelineCheck(channels, checkEvery: 7, ring);
        long first = (long)(start * Rate);
        Expect Expectation(long p) => p < first - 100 ? Expect.Silence : p < first + 100 ? Expect.Anything : Expect.Signal;

        device.StartAt(start);
        double settlingMaxMs = -1;
        while (device.Now < hours * 3600)
        {
            check.ReadUntil(device.CapturePacket(writer), Expectation);
            if (settlingMaxMs < 0 && check.ReadPosition >= 60 * Rate)
            {
                // The first minute is the controller learning the drift; it is checked separately.
                settlingMaxMs = check.MaxErrorMs;
                check.Reset();
            }
        }

        Report(check, writer, $"{driftPpm:+0;-0} ppm drift, ±{jitterMs} ms jitter, {channels} ch, {hours} h; first minute max {settlingMaxMs:0.000} ms");
        Assert.True(settlingMaxMs < 2.0, $"first minute max error {settlingMaxMs:0.000} ms");
        Assert.True(check.MaxErrorMs < 1.0, $"max error {check.MaxErrorMs:0.000} ms after the first minute");
        Assert.True(check.MeanErrorMs < 0.2, $"mean error {check.MeanErrorMs:0.000} ms");
        Assert.Equal(0, check.Holes);
        Assert.Equal(0, check.NoiseInSilence);
        Assert.Equal(0, writer.Resyncs);
        Assert.InRange(writer.CorrectionPpm, driftPpm - 20, driftPpm + 20);
        Assert.True(check.Checked > hours * 3600 * Rate / 7 * 0.9, "most of the timeline was checked");
    }

    [Fact]
    public void FortyFourKilohertzDeviceIsConvertedAndAligned()
    {
        var ring = new TimelineRing();
        var writer = new TimelineWriter(ring, 44_100, 2);
        var device = new SimulatedDevice(driftPpm: 120, jitterMs: 0.3, channels: 2, seed: 3, packetFrames: 441, nominalRate: 44_100);
        var check = new TimelineCheck(2, 3, ring);
        device.StartAt(0.1);
        double settlingMaxMs = -1;
        while (device.Now < 600)
        {
            check.ReadUntil(device.CapturePacket(writer), p => p < (0.1 * Rate) + 100 ? Expect.Anything : Expect.Signal);
            if (settlingMaxMs < 0 && check.ReadPosition >= 60 * Rate)
            {
                settlingMaxMs = check.MaxErrorMs;
                check.Reset();
            }
        }

        Report(check, writer, "44.1 kHz device, +120 ppm, 10 min");
        Assert.True(check.MaxErrorMs < 1.0, $"max error {check.MaxErrorMs:0.000} ms");
        Assert.Equal(0, check.Holes);
        Assert.Equal(0, writer.Resyncs);
    }

    [Fact]
    public void LoopbackSilenceStaysSilentAndAudioResumesInPlace()
    {
        // A loopback stream sends nothing while nothing plays: 20 s of audio, 10 s with no packets, then more audio.
        var ring = new TimelineRing();
        var writer = new TimelineWriter(ring, Rate, 2);
        var device = new SimulatedDevice(driftPpm: 90, jitterMs: 0.3, channels: 2, seed: 11);
        var check = new TimelineCheck(2, 1, ring);
        device.StartAt(1.0);
        while (device.Now < 20)
        {
            check.ReadUntil(device.CapturePacket(writer), _ => Expect.Anything);
        }

        long lastEnd = (long)(device.Now * Rate);
        const double resume = 30.0;
        long resumeAt = (long)(resume * Rate);
        Expect Expectation(long p) =>
            p < lastEnd - 100 ? Expect.Anything
            : p < lastEnd + 100 ? Expect.Anything
            : p < resumeAt - 100 ? Expect.Silence
            : p < resumeAt + 100 ? Expect.Anything
            : Expect.Signal;

        // Nothing is captured during the silence; time goes on and the mixer keeps reading.
        for (double now = device.Now; now < resume; now += 0.01)
        {
            check.ReadUntil(now, Expectation);
        }

        device.StartAt(resume);
        while (device.Now < 120)
        {
            check.ReadUntil(device.CapturePacket(writer), Expectation);
        }

        Report(check, writer, "loopback: 10 s without packets");
        Assert.Equal(0, check.NoiseInSilence);
        Assert.True(check.Checked > 80 * Rate, "the resumed audio was checked");
        Assert.True(check.MaxErrorMs < 1.0, $"max error after resuming {check.MaxErrorMs:0.000} ms");
        Assert.Equal(0, check.Holes);
        Assert.Equal(1, writer.Resyncs);
    }

    [Fact]
    public void LostDeviceLeavesSilenceAndTheReconnectedOneIsAligned()
    {
        // The headset disappears at 40 s; 1.7 s later another device (with a different clock) takes over.
        var firstRing = new TimelineRing();
        var secondRing = new TimelineRing();
        var check = new TimelineCheck(1, 1, firstRing, secondRing);
        var firstWriter = new TimelineWriter(firstRing, Rate, 1);
        var first = new SimulatedDevice(driftPpm: 250, jitterMs: 0.4, channels: 1, seed: 5);
        first.StartAt(0.2);
        while (first.Now < 40)
        {
            check.ReadUntil(first.CapturePacket(firstWriter), _ => Expect.Anything);
        }

        long lostAt = (long)(first.Now * Rate);
        const double reconnect = 41.7;
        long reconnectAt = (long)(reconnect * Rate);
        Expect Expectation(long p) =>
            p < lostAt + 100 ? Expect.Anything
            : p < reconnectAt - 100 ? Expect.Silence
            : p < reconnectAt + 100 ? Expect.Anything
            : Expect.Signal;

        for (double now = first.Now; now < reconnect; now += 0.01)
        {
            check.ReadUntil(now, Expectation);
        }

        var secondWriter = new TimelineWriter(secondRing, Rate, 1);
        var second = new SimulatedDevice(driftPpm: -180, jitterMs: 0.4, channels: 1, seed: 6);
        second.StartAt(reconnect);
        double settlingMaxMs = -1;
        while (second.Now < 400)
        {
            check.ReadUntil(second.CapturePacket(secondWriter), Expectation);
            if (settlingMaxMs < 0 && check.ReadPosition >= (reconnect + 60) * Rate)
            {
                settlingMaxMs = check.MaxErrorMs;
                check.Reset();
            }
        }

        Report(check, secondWriter, $"device lost for 1.7 s, replaced by one 430 ppm apart; first minute max {settlingMaxMs:0.000} ms");
        Assert.Equal(0, check.NoiseInSilence);
        Assert.True(settlingMaxMs < 2.0, $"first minute after reconnecting: max error {settlingMaxMs:0.000} ms");
        Assert.True(check.MaxErrorMs < 1.0, $"max error {check.MaxErrorMs:0.000} ms");
        Assert.Equal(0, check.Holes);
        Assert.Equal(1, firstRing.LastWritten < lostAt + 10 ? 1 : 0);
    }

    [Fact]
    public void DroppedSamplesAboveTheThresholdAreSkippedNotSmeared()
    {
        // The device loses 35 ms of samples (a buffer overflow): the next packet's timestamp is 35 ms later.
        var ring = new TimelineRing();
        var writer = new TimelineWriter(ring, Rate, 1);
        var device = new SimulatedDevice(driftPpm: 60, jitterMs: 0.3, channels: 1, seed: 9);
        var check = new TimelineCheck(1, 1, ring);
        device.StartAt(0.3);
        while (device.Now < 30)
        {
            check.ReadUntil(device.CapturePacket(writer), _ => Expect.Anything);
        }

        double gapStart = device.Now;
        device.StartAt(gapStart + 0.035);
        long gapFrom = (long)(gapStart * Rate);
        long gapTo = (long)((gapStart + 0.035) * Rate);
        Expect Expectation(long p) =>
            p < gapFrom + 100 ? Expect.Anything
            : p < gapTo - 100 ? Expect.Silence
            : p < gapTo + 100 ? Expect.Anything
            : Expect.Signal;
        while (device.Now < 90)
        {
            check.ReadUntil(device.CapturePacket(writer), Expectation);
        }

        Report(check, writer, "35 ms of samples lost");
        Assert.Equal(1, writer.Resyncs);
        Assert.Equal(0, check.NoiseInSilence);
        Assert.True(check.MaxErrorMs < 1.0, $"max error {check.MaxErrorMs:0.000} ms");
        Assert.Equal(0, check.Holes);
    }

    [Fact]
    public void SmallTimestampStepIsSteeredOutWithoutAJump()
    {
        // 8 ms of samples lost: below the 20 ms threshold, so the stream is steered back gradually (no click).
        var ring = new TimelineRing();
        var writer = new TimelineWriter(ring, Rate, 2);
        var device = new SimulatedDevice(driftPpm: -70, jitterMs: 0.3, channels: 2, seed: 13);
        var check = new TimelineCheck(2, 1, ring);
        device.StartAt(0.3);
        while (device.Now < 30)
        {
            check.ReadUntil(device.CapturePacket(writer), _ => Expect.Anything);
        }

        device.StartAt(device.Now + 0.008);
        double stepAt = device.Now;
        double worstDuring = 0;
        while (device.Now < 150)
        {
            check.ReadUntil(device.CapturePacket(writer), p => p < (stepAt + 40) * Rate ? Expect.Anything : Expect.Signal);
            if (check.ReadPosition < (stepAt + 40) * Rate)
            {
                worstDuring = Math.Max(worstDuring, Math.Abs(writer.ErrorMilliseconds));
            }
        }

        Report(check, writer, "8 ms step steered out");
        Assert.Equal(0, writer.Resyncs);
        Assert.True(worstDuring < 9, $"the error stayed near the step: {worstDuring:0.00} ms");
        Assert.True(check.MaxErrorMs < 1.0, $"max error 40 s after the step {check.MaxErrorMs:0.000} ms");
        Assert.Equal(0, check.Holes);
    }

    [Fact]
    public void CorrectionNeverExceedsTheLimit()
    {
        // A wildly wrong clock (1.5 %) cannot be followed by steering; the stream is moved by resyncs instead,
        // and the speed change stays within the inaudible limit.
        var ring = new TimelineRing();
        var writer = new TimelineWriter(ring, Rate, 1);
        var device = new SimulatedDevice(driftPpm: 15_000, jitterMs: 0.2, channels: 1, seed: 17);
        var check = new TimelineCheck(1, 1, ring);
        device.StartAt(0);
        double maxCorrection = 0;
        while (device.Now < 120)
        {
            check.ReadUntil(device.CapturePacket(writer), _ => Expect.Anything);
            maxCorrection = Math.Max(maxCorrection, Math.Abs(writer.CorrectionPpm));
        }

        _output.WriteLine($"1.5 % clock: {writer.Resyncs} resyncs, max correction {maxCorrection:0} ppm");
        Assert.True(maxCorrection <= (TimelineWriter.MaxCorrection * 1e6) + 1e-6);
        Assert.True(writer.Resyncs > 0);
    }

    private void Report(TimelineCheck check, TimelineWriter writer, string scenario) =>
        _output.WriteLine($"{scenario}: max {check.MaxErrorMs:0.0000} ms, mean {check.MeanErrorMs:0.0000} ms over {check.Checked:N0} samples; correction {writer.CorrectionPpm:+0.0;-0.0} ppm; resyncs {writer.Resyncs}; holes {check.Holes}; noise in silence {check.NoiseInSilence}");
}
