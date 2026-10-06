using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace EKrecorder.Recording;

/// <summary>
/// Turns Media Foundation objects into readable text for the log and the report: attribute stores (media types,
/// encoder attributes), samples, their buffers and the GPU textures behind them. When an encoder rejects something,
/// this is how the report shows exactly what was sent.
/// </summary>
internal static unsafe class MediaFoundationDiagnostics
{
    private const ushort VT_R8 = 5;
    private const ushort VT_UNKNOWN = 13;
    private const ushort VT_UI4 = 19;
    private const ushort VT_UI8 = 21;
    private const ushort VT_LPWSTR = 31;
    private const ushort VT_CLSID = 72;
    private const ushort VT_VECTOR_UI1 = 0x1011;

    private static readonly Dictionary<Guid, string> Names = BuildNames();

    /// <summary>Every attribute in the store, "name = value; ...".</summary>
    public static string DescribeAttributes(IMFAttributes* attributes)
    {
        if (attributes == null)
        {
            return "none";
        }

        uint count;
        HRESULT hr = attributes->GetCount(&count);
        if (hr.FAILED)
        {
            return $"unreadable ({MediaFoundation.Describe(hr)})";
        }

        if (count == 0)
        {
            return "empty";
        }

        var items = new List<string>((int)count);
        for (uint i = 0; i < count; i++)
        {
            Guid key;
            PROPVARIANT value = default;
            hr = attributes->GetItemByIndex(i, &key, &value);
            if (hr.FAILED)
            {
                items.Add($"item {i}: unreadable ({MediaFoundation.Describe(hr)})");
                continue;
            }

            try
            {
                items.Add($"{NameOf(key)} = {ValueOf(key, &value)}");
            }
            finally
            {
                PropVariantClear(&value);
            }
        }

        return string.Join("; ", items);
    }

    /// <summary>A sample: times, buffers (lengths, DXGI texture) and the sample's own attributes.</summary>
    public static string DescribeSample(IMFSample* sample)
    {
        if (sample == null)
        {
            return "no sample";
        }

        long time = 0;
        long duration = 0;
        uint buffers = 0;
        uint total = 0;
        sample->GetSampleTime(&time);
        sample->GetSampleDuration(&duration);
        sample->GetBufferCount(&buffers);
        sample->GetTotalLength(&total);
        var text = new StringBuilder(Invariant($"sample time {time}, duration {duration}, {buffers} buffer(s), total length {total:N0} bytes"));
        for (uint i = 0; i < buffers; i++)
        {
            IMFMediaBuffer* buffer = null;
            HRESULT hr = sample->GetBufferByIndex(i, &buffer);
            if (hr.FAILED)
            {
                text.Append(Invariant($"; buffer {i}: unreadable ({MediaFoundation.Describe(hr)})"));
                continue;
            }

            try
            {
                text.Append(Invariant($"; buffer {i}: {DescribeBuffer(buffer)}"));
            }
            finally
            {
                buffer->Release();
            }
        }

        text.Append("; sample attributes: ").Append(DescribeAttributes((IMFAttributes*)sample));
        return text.ToString();
    }

    /// <summary>A buffer: maximum, current and contiguous length, and its texture when it is a DXGI buffer.</summary>
    public static string DescribeBuffer(IMFMediaBuffer* buffer)
    {
        uint maxLength = 0;
        uint currentLength = 0;
        buffer->GetMaxLength(&maxLength);
        buffer->GetCurrentLength(&currentLength);
        var text = new StringBuilder(Invariant($"max length {maxLength:N0}, current length {currentLength:N0}"));

        IMF2DBuffer* buffer2d;
        if (buffer->QueryInterface(__uuidof<IMF2DBuffer>(), (void**)&buffer2d).SUCCEEDED)
        {
            uint contiguous = 0;
            buffer2d->GetContiguousLength(&contiguous);
            buffer2d->Release();
            text.Append(Invariant($", contiguous length {contiguous:N0}"));
        }

        IMFDXGIBuffer* dxgiBuffer;
        if (buffer->QueryInterface(__uuidof<IMFDXGIBuffer>(), (void**)&dxgiBuffer).SUCCEEDED)
        {
            uint subresource = 0;
            dxgiBuffer->GetSubresourceIndex(&subresource);
            ID3D11Texture2D* texture;
            if (dxgiBuffer->GetResource(__uuidof<ID3D11Texture2D>(), (void**)&texture).SUCCEEDED)
            {
                text.Append(Invariant($", DXGI buffer on texture 0x{(nint)texture:X} subresource {subresource}: {DescribeTexture(texture)}"));
                texture->Release();
            }
            else
            {
                text.Append(Invariant($", DXGI buffer (texture unreadable), subresource {subresource}"));
            }

            dxgiBuffer->Release();
        }
        else
        {
            text.Append(", system-memory buffer");
        }

        return text.ToString();
    }

    /// <summary>Format, size, usage, bind flags and the other creation flags of a texture.</summary>
    public static string DescribeTexture(ID3D11Texture2D* texture)
    {
        D3D11_TEXTURE2D_DESC description;
        texture->GetDesc(&description);
        return Invariant($"{description.Format} {description.Width}x{description.Height}, usage {description.Usage}, bind {BindFlagsText(description.BindFlags)}, CPU access 0x{description.CPUAccessFlags:X}, misc 0x{description.MiscFlags:X}, array size {description.ArraySize}, mip levels {description.MipLevels}, sample count {description.SampleDesc.Count}");
    }

    /// <summary>D3D11 bind flags as names, for example "RENDER_TARGET | VIDEO_ENCODER (0x420)".</summary>
    public static string BindFlagsText(uint flags)
    {
        (uint Flag, string Name)[] known =
        [
            (0x1, "VERTEX_BUFFER"), (0x2, "INDEX_BUFFER"), (0x4, "CONSTANT_BUFFER"), (0x8, "SHADER_RESOURCE"),
            (0x10, "STREAM_OUTPUT"), (0x20, "RENDER_TARGET"), (0x40, "DEPTH_STENCIL"), (0x80, "UNORDERED_ACCESS"),
            (0x200, "DECODER"), (0x400, "VIDEO_ENCODER"),
        ];
        var names = known.Where(k => (flags & k.Flag) != 0).Select(k => k.Name).ToList();
        uint unknown = flags & ~known.Aggregate(0u, (all, k) => all | k.Flag);
        if (unknown != 0)
        {
            names.Add(Invariant($"0x{unknown:X}"));
        }

        return names.Count == 0 ? "none (0x0)" : Invariant($"{string.Join(" | ", names)} (0x{flags:X})");
    }

    private static string NameOf(Guid key) => Names.TryGetValue(key, out string? name) ? name : key.ToString("B");

    private static string ValueOf(Guid key, PROPVARIANT* value)
    {
        byte* data = (byte*)value + 8;
        ushort type = *(ushort*)value;
        switch (type)
        {
            case VT_UI4:
            {
                uint number = *(uint*)data;
                return key == MF.MF_SA_D3D11_BINDFLAGS ? BindFlagsText(number)
                    : key == MF.MF_SA_D3D11_USAGE ? $"{(D3D11_USAGE)number}"
                    : key == MF.MF_MT_DEFAULT_STRIDE ? Invariant($"{(int)number}")
                    : Invariant($"{number}");
            }

            case VT_UI8:
            {
                ulong number = *(ulong*)data;
                uint high = (uint)(number >> 32);
                uint low = (uint)number;
                return key == MF.MF_MT_FRAME_SIZE ? Invariant($"{high}x{low}")
                    : key == MF.MF_MT_FRAME_RATE || key == MF.MF_MT_PIXEL_ASPECT_RATIO
                        || key == MF.MF_MT_FRAME_RATE_RANGE_MIN || key == MF.MF_MT_FRAME_RATE_RANGE_MAX ? Invariant($"{high}/{low}")
                    : Invariant($"{number}");
            }

            case VT_R8:
                return (*(double*)data).ToString(CultureInfo.InvariantCulture);
            case VT_CLSID:
                Guid* guid = *(Guid**)data;
                return guid == null ? "null" : NameOf(*guid);
            case VT_LPWSTR:
                char* text = *(char**)data;
                return text == null ? "null" : $"\"{new string(text)}\"";
            case VT_VECTOR_UI1:
                return Invariant($"{*(uint*)data} bytes");
            case VT_UNKNOWN:
                return "(object)";
            default:
                return Invariant($"(variant type {type})");
        }
    }

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);

    private static Dictionary<Guid, string> BuildNames()
    {
        var names = new Dictionary<Guid, string>();

        // The name is the argument's source text, so each GUID is written once.
        void Add(Guid guid, [CallerArgumentExpression(nameof(guid))] string expression = "") =>
            names.TryAdd(guid, expression[(expression.LastIndexOf('.') + 1)..]);

        Add(MF.MF_MT_MAJOR_TYPE);
        Add(MF.MF_MT_SUBTYPE);
        Add(MF.MF_MT_ALL_SAMPLES_INDEPENDENT);
        Add(MF.MF_MT_FIXED_SIZE_SAMPLES);
        Add(MF.MF_MT_COMPRESSED);
        Add(MF.MF_MT_SAMPLE_SIZE);
        Add(MF.MF_MT_VIDEO_ROTATION);
        Add(MF.MF_MT_FRAME_SIZE);
        Add(MF.MF_MT_FRAME_RATE);
        Add(MF.MF_MT_PIXEL_ASPECT_RATIO);
        Add(MF.MF_MT_VIDEO_CHROMA_SITING);
        Add(MF.MF_MT_INTERLACE_MODE);
        Add(MF.MF_MT_TRANSFER_FUNCTION);
        Add(MF.MF_MT_VIDEO_PRIMARIES);
        Add(MF.MF_MT_YUV_MATRIX);
        Add(MF.MF_MT_VIDEO_LIGHTING);
        Add(MF.MF_MT_VIDEO_NOMINAL_RANGE);
        Add(MF.MF_MT_GEOMETRIC_APERTURE);
        Add(MF.MF_MT_MINIMUM_DISPLAY_APERTURE);
        Add(MF.MF_MT_PAN_SCAN_APERTURE);
        Add(MF.MF_MT_AVG_BITRATE);
        Add(MF.MF_MT_AVG_BIT_ERROR_RATE);
        Add(MF.MF_MT_MAX_KEYFRAME_SPACING);
        Add(MF.MF_MT_USER_DATA);
        Add(MF.MF_MT_REALTIME_CONTENT);
        Add(MF.MF_MT_DEFAULT_STRIDE);
        Add(MF.MF_MT_VIDEO_PROFILE);
        Add(MF.MF_MT_VIDEO_LEVEL);
        Add(MF.MF_MT_MPEG2_PROFILE);
        Add(MF.MF_MT_MPEG2_LEVEL);
        Add(MF.MF_MT_MPEG_SEQUENCE_HEADER);
        Add(MF.MF_MT_H264_MAX_CODEC_CONFIG_DELAY);
        Add(MF.MF_MT_H264_SUPPORTED_RATE_CONTROL_MODES);
        Add(MF.MF_MT_H264_SUPPORTED_USAGES);
        Add(MF.MF_MT_H264_CAPABILITIES);
        Add(MF.MF_MT_FRAME_RATE_RANGE_MIN);
        Add(MF.MF_MT_FRAME_RATE_RANGE_MAX);
        Add(MF.MF_MT_ALPHA_MODE);
        Add(MF.MF_MT_SECURE);
        Add(MF.MF_LOW_LATENCY);
        Add(MF.MF_NALU_LENGTH_SET);
        Add(MF.MF_SA_D3D_AWARE);
        Add(MF.MF_SA_D3D11_AWARE);
        Add(MF.MF_SA_D3D11_BINDFLAGS);
        Add(MF.MF_SA_D3D11_USAGE);
        Add(MF.MF_SA_D3D11_SHARED);
        Add(MF.MF_SA_D3D11_SHARED_WITHOUT_MUTEX);
        Add(MF.MF_SA_D3D11_ALLOW_DYNAMIC_YUV_TEXTURE);
        Add(MF.MF_SA_D3D11_HW_PROTECTED);
        Add(MF.MF_SA_D3D11_ALLOCATE_DISPLAYABLE_RESOURCES);
        Add(MF.MF_SA_BUFFERS_PER_SAMPLE);
        Add(MF.MF_SA_REQUIRED_SAMPLE_COUNT);
        Add(MF.MF_SA_REQUIRED_SAMPLE_COUNT_PROGRESSIVE);
        Add(MF.MF_SA_MINIMUM_OUTPUT_SAMPLE_COUNT);
        Add(MF.MF_SA_MINIMUM_OUTPUT_SAMPLE_COUNT_PROGRESSIVE);
        Add(MF.MF_TRANSFORM_ASYNC);
        Add(MF.MF_TRANSFORM_ASYNC_UNLOCK);
        Add(MFT.MFT_ENUM_HARDWARE_URL_Attribute);
        Add(MFT.MFT_ENUM_HARDWARE_VENDOR_ID_Attribute);
        Add(MFT.MFT_FRIENDLY_NAME_Attribute);
        Add(MFT.MFT_ENUM_ADAPTER_LUID);
        Add(MFT.MFT_SUPPORT_DYNAMIC_FORMAT_CHANGE);
        Add(MFT.MFT_GFX_DRIVER_VERSION_ID_Attribute);
        Add(MFT.MFT_CODEC_MERIT_Attribute);
        Add(MFT.MFT_ENCODER_SUPPORTS_CONFIG_EVENT);
        Add(MFT.MFT_PREFERRED_OUTPUTTYPE_Attribute);
        Add(MFSampleExtension_CleanPoint);
        Add(MFSampleExtension_Discontinuity);
        Add(MFSampleExtension_DecodeTimestamp);
        Add(MFSampleExtension_Interlaced);
        Add(MFSampleExtension_Token);
        Add(MFSampleExtension_VideoEncodePictureType);
        Add(MFSampleExtension_VideoEncodeQP);
        Add(MFSampleExtension_FrameCorruption);

        // Values that are GUIDs.
        Add(MFMediaType_Video);
        Add(MFVideoFormat.MFVideoFormat_NV12);
        Add(MFVideoFormat.MFVideoFormat_H264);
        Add(MFVideoFormat.MFVideoFormat_ARGB32);
        Add(MFVideoFormat.MFVideoFormat_RGB32);
        Add(MFVideoFormat.MFVideoFormat_YUY2);
        Add(MFVideoFormat.MFVideoFormat_I420);
        Add(MFVideoFormat.MFVideoFormat_IYUV);
        Add(MFVideoFormat.MFVideoFormat_P010);
        return names;
    }
}
