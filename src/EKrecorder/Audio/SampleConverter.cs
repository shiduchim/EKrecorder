namespace EKrecorder.Audio;

/// <summary>How the samples in a capture stream's packets are stored.</summary>
internal enum SampleType
{
    Float32,
    Int16,
    Int24,
    Int32,
}

/// <summary>A capture stream's packet format, as WASAPI describes it.</summary>
/// <param name="ChannelMask">Speaker positions (WAVEFORMATEXTENSIBLE), or 0 when the format does not say.</param>
internal sealed record CaptureFormat(SampleType Type, int Channels, int SampleRate, uint ChannelMask)
{
    public int BytesPerSample => Type switch
    {
        SampleType.Int16 => 2,
        SampleType.Int24 => 3,
        _ => 4,
    };

    public int BytesPerFrame => BytesPerSample * Channels;

    public string Describe() => $"{SampleRate} Hz, {Channels} channel(s), {Type switch { SampleType.Float32 => "32-bit float", SampleType.Int16 => "16-bit", SampleType.Int24 => "24-bit", _ => "32-bit" }}";
}

/// <summary>
/// Turns one packet of any PCM or float format and channel count into float frames with one channel (the
/// microphone: all channels averaged) or two (computer audio: mono doubled, stereo kept, surround folded down).
/// Allocation-free, for the capture thread.
/// </summary>
internal sealed class SampleConverter
{
    private const float Minus3dB = 0.70710677f;

    // Speaker bits in WAVEFORMATEXTENSIBLE.dwChannelMask, in the order channels appear in a packet.
    private static readonly (uint Bit, float Left, float Right)[] Speakers =
    [
        (0x1, 1f, 0f),              // front left
        (0x2, 0f, 1f),              // front right
        (0x4, Minus3dB, Minus3dB),  // front centre
        (0x8, 0f, 0f),              // low frequency: left out of a stereo fold-down
        (0x10, Minus3dB, 0f),       // back left
        (0x20, 0f, Minus3dB),       // back right
        (0x40, 1f, 0f),             // front left of centre
        (0x80, 0f, 1f),             // front right of centre
        (0x100, 0.5f, 0.5f),        // back centre
        (0x200, Minus3dB, 0f),      // side left
        (0x400, 0f, Minus3dB),      // side right
    ];

    private readonly CaptureFormat _format;
    private readonly int _outputChannels;
    private readonly float[] _left;
    private readonly float[] _right;

    /// <param name="outputChannels">1 or 2.</param>
    public SampleConverter(CaptureFormat format, int outputChannels)
    {
        if (outputChannels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(outputChannels), outputChannels, "1 or 2 channels");
        }

        if (format.Channels < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(format), format.Channels, "at least one channel");
        }

        _format = format;
        _outputChannels = outputChannels;
        _left = new float[format.Channels];
        _right = new float[format.Channels];
        if (outputChannels == 1)
        {
            Array.Fill(_left, 1f / format.Channels);
        }
        else if (format.Channels == 1)
        {
            _left[0] = _right[0] = 1f;
        }
        else if (format.Channels == 2)
        {
            _left[0] = 1f;
            _right[1] = 1f;
        }
        else
        {
            FoldDown(format.Channels, format.ChannelMask == 0 ? 0x63Fu : format.ChannelMask);
        }
    }

    public int OutputChannels => _outputChannels;

    /// <summary>
    /// Converts <paramref name="frames"/> frames from <paramref name="data"/> into <paramref name="output"/>
    /// (interleaved, <see cref="OutputChannels"/> per frame). A null <paramref name="data"/> means silence.
    /// </summary>
    /// <param name="allZero">True when every input sample was exactly zero (digital silence).</param>
    /// <returns>The highest absolute output value.</returns>
    public unsafe float Convert(byte* data, int frames, Span<float> output, out bool allZero)
    {
        int count = frames * _outputChannels;
        if (data == null)
        {
            output[..count].Clear();
            allZero = true;
            return 0;
        }

        int channels = _format.Channels;
        int bytesPerSample = _format.BytesPerSample;
        bool anyNonZero = false;
        float peak = 0;
        byte* p = data;
        for (int f = 0; f < frames; f++)
        {
            float left = 0;
            float right = 0;
            for (int c = 0; c < channels; c++)
            {
                float sample = Read(p);
                p += bytesPerSample;
                if (sample != 0)
                {
                    anyNonZero = true;
                }

                left += sample * _left[c];
                right += sample * _right[c];
            }

            if (_outputChannels == 1)
            {
                output[f] = left;
                peak = Math.Max(peak, Math.Abs(left));
            }
            else
            {
                output[2 * f] = left;
                output[(2 * f) + 1] = right;
                peak = Math.Max(peak, Math.Max(Math.Abs(left), Math.Abs(right)));
            }
        }

        allZero = !anyNonZero;
        return peak;
    }

    private unsafe float Read(byte* p) => _format.Type switch
    {
        // A misbehaving driver can deliver NaN or infinity; that becomes silence, not a full-scale click.
        SampleType.Float32 => float.IsFinite(*(float*)p) ? *(float*)p : 0f,
        SampleType.Int16 => *(short*)p / 32768f,
        SampleType.Int24 => (((p[0] | (p[1] << 8) | (p[2] << 16)) << 8) >> 8) / 8388608f,
        _ => *(int*)p / 2147483648f,
    };

    private void FoldDown(int channels, uint mask)
    {
        int channel = 0;
        foreach ((uint bit, float left, float right) in Speakers)
        {
            if ((mask & bit) != 0 && channel < channels)
            {
                _left[channel] = left;
                _right[channel] = right;
                channel++;
            }
        }

        // Channels the mask does not name (or names beyond the table) go to both sides, quietly.
        for (; channel < channels; channel++)
        {
            _left[channel] = 0.5f;
            _right[channel] = 0.5f;
        }
    }
}
