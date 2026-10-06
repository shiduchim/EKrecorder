using EKrecorder.Audio;

namespace EKrecorder.Tests;

public sealed class TimelineRingTests
{
    [Fact]
    public void PositionsNobodyWroteReadAsSilence()
    {
        var ring = new TimelineRing();
        ring.Write(10, 0.5f, -0.5f);
        var left = new float[32];
        var right = new float[32];
        ring.MixInto(0, left, right);
        for (int i = 0; i < 32; i++)
        {
            Assert.Equal(i == 10 ? 0.5f : 0f, left[i]);
            Assert.Equal(i == 10 ? -0.5f : 0f, right[i]);
        }
    }

    [Fact]
    public void LateSamplesAreDroppedAndCounted()
    {
        var ring = new TimelineRing();
        ring.MixInto(0, new float[100], new float[100]);
        Assert.False(ring.Write(99, 1f, 1f));
        Assert.True(ring.Write(100, 1f, 1f));
        Assert.Equal(1, ring.LateSamples);
    }

    [Fact]
    public void NothingStaleIsReadWhenTheRingWraps()
    {
        var ring = new TimelineRing();
        ring.Write(5, 0.25f, 0.25f);
        var left = new float[1000];
        var right = new float[1000];
        ring.MixInto(0, left, right);
        Assert.Equal(0.25f, left[5]);

        // Read all the way around without writing: the slot that held position 5 now stands for 5 + Capacity.
        long position = 1000;
        while (position < TimelineRing.Capacity)
        {
            int count = (int)Math.Min(1000, TimelineRing.Capacity - position);
            ring.MixInto(position, left.AsSpan(0, count), right.AsSpan(0, count));
            position += count;
        }

        Array.Clear(left);
        Array.Clear(right);
        ring.MixInto(TimelineRing.Capacity, left, right);
        Assert.All(left, v => Assert.Equal(0f, v));
        Assert.All(right, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void SamplesThatWouldOverwriteUnreadOnesAreDropped()
    {
        var ring = new TimelineRing();
        Assert.True(ring.Write(TimelineRing.Capacity - 1, 1f, 1f));
        Assert.False(ring.Write(TimelineRing.Capacity, 1f, 1f));
        Assert.Equal(1, ring.OverrunSamples);
    }

    [Fact]
    public void DrainedOnlyAfterTheMixerPassedTheLastSample()
    {
        var ring = new TimelineRing();
        Assert.True(ring.IsDrained);
        ring.Write(500, 1f, 1f);
        Assert.False(ring.IsDrained);
        ring.MixInto(0, new float[500], new float[500]);
        Assert.False(ring.IsDrained);
        ring.MixInto(500, new float[1], new float[1]);
        Assert.True(ring.IsDrained);
    }

    [Fact]
    public void SumAddsAllFeeds()
    {
        var mic = new TimelineRing();
        var computer = new TimelineRing();
        mic.Write(0, 0.2f, 0.2f);
        computer.Write(0, 0.1f, -0.3f);
        computer.Write(1, 0.4f, 0.4f);
        var left = new float[] { 9, 9 };
        var right = new float[] { 9, 9 };
        AudioMix.Sum(new[] { mic, computer }, 0, left, right);
        Assert.Equal(0.3f, left[0], 1e-6f);
        Assert.Equal(-0.1f, right[0], 1e-6f);
        Assert.Equal(0.4f, left[1]);
        Assert.Equal(0.4f, right[1]);
    }
}

public sealed class PeakLimiterTests
{
    [Fact]
    public void NoSampleEverExceedsTheCeiling()
    {
        // Microphone and computer audio both at full scale, plus random peaks well above it.
        var limiter = new PeakLimiter();
        var random = new Random(3);
        var left = new float[48_000];
        var right = new float[48_000];
        for (int block = 0; block < 20; block++)
        {
            for (int i = 0; i < left.Length; i++)
            {
                double t = ((block * left.Length) + i) / 48_000.0;
                float voice = (float)Math.Sin(2 * Math.PI * 220 * t);
                float music = (float)Math.Sin(2 * Math.PI * 330 * t);
                float spike = random.NextDouble() < 0.001 ? (float)(random.NextDouble() * 6) : 0f;
                left[i] = voice + music + spike;
                right[i] = voice - music - spike;
            }

            limiter.Process(left, right);
            Assert.All(left, v => Assert.InRange(v, -PeakLimiter.Ceiling, PeakLimiter.Ceiling));
            Assert.All(right, v => Assert.InRange(v, -PeakLimiter.Ceiling, PeakLimiter.Ceiling));
        }

        Assert.True(limiter.LimitedSamples > 0);
        Assert.True(limiter.MaxReductionDb > 6);
    }

    [Fact]
    public void OrdinaryLevelsPassUnchanged()
    {
        var limiter = new PeakLimiter();
        var left = new float[4800];
        var right = new float[4800];
        for (int i = 0; i < left.Length; i++)
        {
            left[i] = 0.5f * (float)Math.Sin(i * 0.05);
            right[i] = -left[i];
        }

        float[] original = (float[])left.Clone();
        limiter.Process(left, right);
        Assert.Equal(original, left);
        Assert.Equal(0, limiter.LimitedSamples);
    }

    [Fact]
    public void GainRecoversAfterAPeak()
    {
        var limiter = new PeakLimiter();
        var left = new float[48_000];
        var right = new float[48_000];
        left[0] = right[0] = 4f;
        for (int i = 1; i < left.Length; i++)
        {
            left[i] = right[i] = 0.5f;
        }

        limiter.Process(left, right);
        Assert.Equal(PeakLimiter.Ceiling, left[0], 1e-6f);
        Assert.True(left[100] < 0.5f, "still turned down right after the peak");
        Assert.Equal(0.5f, left[^1], 1e-3f);
    }

    [Fact]
    public void Pcm16RoundsAndSaturates()
    {
        float[] left = { 0f, 1f, -1f, 0.5f, 2f, -2f };
        float[] right = { 0.25f, -0.25f, 0f, 0f, 0f, 0f };
        var pcm = new short[12];
        AudioMix.ToPcm16(left, right, pcm);
        Assert.Equal(new short[] { 0, 8192, 32767, -8192, -32767, 0, 16384, 0, 32767, 0, -32768, 0 }, pcm);
    }
}

public sealed unsafe class SampleConverterTests
{
    [Fact]
    public void StereoInt16MicrophoneIsAveragedToMono()
    {
        var converter = new SampleConverter(new CaptureFormat(SampleType.Int16, 2, 48_000, 0), 1);
        short[] data = { 16384, 0, -16384, -16384, 0, 0 };
        var output = new float[3];
        fixed (short* p = data)
        {
            float peak = converter.Convert((byte*)p, 3, output, out bool allZero);
            Assert.False(allZero);
            Assert.Equal(0.5f, peak);
        }

        Assert.Equal(new[] { 0.25f, -0.5f, 0f }, output);
    }

    [Fact]
    public void MonoIsDoubledForTheComputerAudioMix()
    {
        var converter = new SampleConverter(new CaptureFormat(SampleType.Float32, 1, 48_000, 0), 2);
        float[] data = { 0.1f, -0.2f };
        var output = new float[4];
        fixed (float* p = data)
        {
            converter.Convert((byte*)p, 2, output, out _);
        }

        Assert.Equal(new[] { 0.1f, 0.1f, -0.2f, -0.2f }, output);
    }

    [Fact]
    public void FivePointOneFoldsDownToStereo()
    {
        // FL FR FC LFE BL BR
        var converter = new SampleConverter(new CaptureFormat(SampleType.Float32, 6, 48_000, 0x3F), 2);
        float[] data = { 0.1f, 0.2f, 0.3f, 0.9f, 0.4f, 0.5f };
        var output = new float[2];
        fixed (float* p = data)
        {
            converter.Convert((byte*)p, 1, output, out _);
        }

        const float c = 0.70710677f;
        Assert.Equal(0.1f + (c * 0.3f) + (c * 0.4f), output[0], 1e-6f);
        Assert.Equal(0.2f + (c * 0.3f) + (c * 0.5f), output[1], 1e-6f);
    }

    [Fact]
    public void ExactZeroIsReportedAsDigitalSilence()
    {
        var converter = new SampleConverter(new CaptureFormat(SampleType.Float32, 2, 48_000, 0), 1);
        float[] data = new float[960];
        var output = new float[480];
        fixed (float* p = data)
        {
            converter.Convert((byte*)p, 480, output, out bool allZero);
            Assert.True(allZero);
            data[333] = 1e-9f;
            converter.Convert((byte*)p, 480, output, out allZero);
            Assert.False(allZero);
        }

        converter.Convert(null, 480, output, out bool silent);
        Assert.True(silent);
        Assert.All(output, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void NaNAndInfinityBecomeSilence()
    {
        var converter = new SampleConverter(new CaptureFormat(SampleType.Float32, 1, 48_000, 0), 1);
        float[] data = { float.NaN, float.PositiveInfinity, 0.5f };
        var output = new float[3];
        fixed (float* p = data)
        {
            converter.Convert((byte*)p, 3, output, out _);
        }

        Assert.Equal(new[] { 0f, 0f, 0.5f }, output);
    }

    [Fact]
    public void Int24IsSignExtended()
    {
        var converter = new SampleConverter(new CaptureFormat(SampleType.Int24, 1, 48_000, 0), 1);
        byte[] data = { 0x00, 0x00, 0x80, 0xFF, 0xFF, 0x7F };
        var output = new float[2];
        fixed (byte* p = data)
        {
            converter.Convert(p, 2, output, out _);
        }

        Assert.Equal(-1f, output[0]);
        Assert.Equal(8388607f / 8388608f, output[1]);
    }
}

public sealed class TimelineClockTests
{
    [Fact]
    public void QpcConversionDoesNotOverflowAfterWeeksOfUptime()
    {
        long frequency = 3_579_545;
        long ticks = frequency * 3600L * 24 * 60; // 60 days
        Assert.Equal(10_000_000L * 3600 * 24 * 60, TimelineClock.ToHns(ticks, frequency));
    }

    [Fact]
    public void ChunkTimesAreExactAndContiguous()
    {
        long previous = 0;
        for (long position = 960; position <= 48_000L * 3600 * 3; position += 960)
        {
            long time = TimelineClock.TimeOf(position);
            Assert.Equal(200_000, time - previous);
            previous = time;
        }
    }

    [Fact]
    public void PositionsFollowTheClock()
    {
        var clock = new TimelineClock(startHns: 5_000_000);
        Assert.Equal(0, clock.PositionOf(5_000_000));
        Assert.Equal(48_000, clock.PositionOf(15_000_000), 9);
        Assert.Equal(-4_800, clock.PositionOf(4_000_000), 9);
    }
}

public sealed class OutputSizeTests
{
    [Theory]
    [InlineData(3840, 2160, 1920, 1080)]
    [InlineData(2560, 1440, 1920, 1080)]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(1366, 768, 1366, 768)]
    [InlineData(1365, 767, 1364, 766)]
    [InlineData(1080, 1920, 1080, 1920)]
    [InlineData(2160, 3840, 1080, 1920)]
    [InlineData(3440, 1440, 1920, 804)]
    [InlineData(5120, 1440, 1920, 540)]
    public void FitsWithoutUpscalingAndKeepsEvenSides(int width, int height, int expectedWidth, int expectedHeight)
    {
        // The 1080p step.
        Size size = EKrecorder.Recording.RecordingQuality.Preset(3, 2).OutputSizeFor(new Size(width, height));
        Assert.Equal(new Size(expectedWidth, expectedHeight), size);
    }
}
