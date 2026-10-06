using System.Globalization;
using EKrecorder.Diagnostics;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static EKrecorder.Recording.MediaFoundation;
using static TerraFX.Interop.Windows.Windows;

namespace EKrecorder.Recording;

/// <summary>
/// NV12 video samples backed by GPU textures, handed out by Media Foundation's video sample allocator. A sample goes
/// back to the pool only when the encoder has released it, so a texture is never overwritten while the encoder still
/// reads it. When every sample is still with the encoder, <see cref="TryTake"/> returns false and that frame is dropped.
/// <para>
/// The allocator's samples can come with a buffer whose current length is 0, and the sink writer rejects a sample of
/// length 0 (IMFSinkWriter::WriteSample fails with E_INVALIDARG before the encoder sees it). So every sample gets its
/// buffer length set to the frame size before use, as in the usual MFCreateDXGISurfaceBuffer pattern:
/// IMF2DBuffer::GetContiguousLength, then IMFMediaBuffer::SetCurrentLength.
/// </para>
/// </summary>
internal sealed unsafe class GpuSamplePool : IDisposable
{
    private const uint InitialSamples = 4;
    private const uint MaximumSamples = 10;
    private const int MF_E_SAMPLEALLOCATOR_EMPTY = unchecked((int)0xC00D4A3E);

    // The first samples are logged one by one; after that, only a texture the pool has not used before, or one whose
    // description changed. The pool recycles the same few textures, so every one of them is still logged.
    private const int SamplesLoggedInFull = 30;

    private readonly uint _frameBytes;
    private readonly Dictionary<(nint Texture, uint Subresource), string> _seen = new();
    private IMFVideoSampleAllocatorEx* _allocator;
    private long _taken;
    private long _takenWithZeroLength;
    private string? _firstSample;
    private bool _lengthWarned;

    private GpuSamplePool(IMFVideoSampleAllocatorEx* allocator, Size size, uint bindFlags)
    {
        _allocator = allocator;
        _frameBytes = (uint)(size.Width * size.Height * 3 / 2);
        BindFlags = bindFlags;
    }

    /// <summary>The D3D11 bind flags the pool's textures were created with.</summary>
    public uint BindFlags { get; }

    /// <summary>For the report: how many samples were used, their buffer lengths, and their textures.</summary>
    public string Summary => _taken == 0
        ? "no GPU sample was used"
        : string.Create(CultureInfo.InvariantCulture,
            $"{_taken:N0} taken from the pool, {_seen.Count} different textures; {_takenWithZeroLength:N0} came with current length 0, every one was set to the frame size; first one: {_firstSample}");

    /// <param name="bindFlags">D3D11 bind flags for the textures (RENDER_TARGET at least: the video processor draws into them).</param>
    /// <param name="sharedWithoutMutex">Create shareable textures, when the encoder asks for that.</param>
    public static GpuSamplePool? TryCreate(IMFDXGIDeviceManager* manager, Size size, int framesPerSecond, uint bindFlags, bool sharedWithoutMutex)
    {
        IMFVideoSampleAllocatorEx* allocator = null;
        IMFAttributes* attributes = null;
        IMFMediaType* type = null;
        try
        {
            Check(MFCreateVideoSampleAllocatorEx(__uuidof<IMFVideoSampleAllocatorEx>(), (void**)&allocator), "MFCreateVideoSampleAllocatorEx");
            Check(allocator->SetDirectXManager((IUnknown*)manager), "IMFVideoSampleAllocatorEx::SetDirectXManager");
            Check(MFCreateAttributes(&attributes, 3), "MFCreateAttributes");
            Check(attributes->SetUINT32(Ptr(in MF.MF_SA_D3D11_BINDFLAGS), bindFlags), "set MF_SA_D3D11_BINDFLAGS");
            Check(attributes->SetUINT32(Ptr(in MF.MF_SA_D3D11_USAGE), (uint)D3D11_USAGE.D3D11_USAGE_DEFAULT), "set MF_SA_D3D11_USAGE");
            if (sharedWithoutMutex)
            {
                Check(attributes->SetUINT32(Ptr(in MF.MF_SA_D3D11_SHARED_WITHOUT_MUTEX), 1), "set MF_SA_D3D11_SHARED_WITHOUT_MUTEX");
            }

            type = CreateVideoType(in MFVideoFormat.MFVideoFormat_NV12, size.Width, size.Height, framesPerSecond);
            Check(allocator->InitializeSampleAllocatorEx(InitialSamples, MaximumSamples, attributes, type), "IMFVideoSampleAllocatorEx::InitializeSampleAllocatorEx");
            Log.Info($"GPU sample pool: {InitialSamples} to {MaximumSamples} NV12 {size.Width}x{size.Height} textures, bind {MediaFoundationDiagnostics.BindFlagsText(bindFlags)}, usage DEFAULT{(sharedWithoutMutex ? ", shared without mutex" : "")}");
            var pool = new GpuSamplePool(allocator, size, bindFlags);
            allocator = null;
            return pool;
        }
        catch (MediaFoundationException ex)
        {
            Log.Error($"Could not create GPU video samples (bind {MediaFoundationDiagnostics.BindFlagsText(bindFlags)})", ex);
            return null;
        }
        finally
        {
            if (type != null)
            {
                type->Release();
            }

            if (attributes != null)
            {
                attributes->Release();
            }

            if (allocator != null)
            {
                allocator->Release();
            }
        }
    }

    /// <summary>
    /// A free sample, ready for the encoder once its texture is drawn, and that texture (both AddRef'ed; release
    /// both). False when every sample is still with the encoder.
    /// </summary>
    public bool TryTake(out IMFSample* sample, out ID3D11Texture2D* texture, out uint subresource)
    {
        sample = null;
        texture = null;
        subresource = 0;
        IMFSample* taken;
        HRESULT hr = _allocator->AllocateSample(&taken);
        if (hr.Value == MF_E_SAMPLEALLOCATOR_EMPTY)
        {
            return false;
        }

        Check(hr, "IMFVideoSampleAllocatorEx::AllocateSample");
        IMFMediaBuffer* buffer = null;
        IMFDXGIBuffer* dxgiBuffer = null;
        ID3D11Texture2D* resource = null;
        try
        {
            Check(taken->GetBufferByIndex(0, &buffer), "IMFSample::GetBufferByIndex");
            SetFrameLength(buffer, out uint maxLength, out uint lengthBefore, out uint contiguousLength, out uint length);
            Check(buffer->QueryInterface(__uuidof<IMFDXGIBuffer>(), (void**)&dxgiBuffer), "IMFMediaBuffer::QueryInterface(IMFDXGIBuffer)");
            Check(dxgiBuffer->GetResource(__uuidof<ID3D11Texture2D>(), (void**)&resource), "IMFDXGIBuffer::GetResource");
            uint index;
            Check(dxgiBuffer->GetSubresourceIndex(&index), "IMFDXGIBuffer::GetSubresourceIndex");
            Note(resource, index, maxLength, lengthBefore, contiguousLength, length);

            sample = taken;
            texture = resource;
            subresource = index;
            taken = null;
            resource = null;
            return true;
        }
        finally
        {
            if (resource != null)
            {
                resource->Release();
            }

            if (dxgiBuffer != null)
            {
                dxgiBuffer->Release();
            }

            if (buffer != null)
            {
                buffer->Release();
            }

            if (taken != null)
            {
                taken->Release();
            }
        }
    }

    public void Dispose()
    {
        if (_allocator != null)
        {
            _allocator->UninitializeSampleAllocator();
            _allocator->Release();
            _allocator = null;
        }
    }

    /// <summary>Sets the buffer's current length to the frame size (the contiguous NV12 length).</summary>
    private void SetFrameLength(IMFMediaBuffer* buffer, out uint maxLength, out uint lengthBefore, out uint contiguousLength, out uint length)
    {
        uint max;
        uint before;
        Check(buffer->GetMaxLength(&max), "IMFMediaBuffer::GetMaxLength");
        Check(buffer->GetCurrentLength(&before), "IMFMediaBuffer::GetCurrentLength");
        uint contiguous = 0;
        IMF2DBuffer* buffer2d;
        if (buffer->QueryInterface(__uuidof<IMF2DBuffer>(), (void**)&buffer2d).SUCCEEDED)
        {
            if (buffer2d->GetContiguousLength(&contiguous).FAILED)
            {
                contiguous = 0;
            }

            buffer2d->Release();
        }

        // The contiguous length is the NV12 frame size. Use the maximum length, then the computed size, only if the
        // buffer does not report it; never more than the maximum.
        length = contiguous != 0 && (max == 0 || contiguous <= max) ? contiguous
            : max != 0 ? max
            : _frameBytes;
        Check(buffer->SetCurrentLength(length), $"IMFMediaBuffer::SetCurrentLength({length})");
        maxLength = max;
        lengthBefore = before;
        contiguousLength = contiguous;
    }

    private void Note(ID3D11Texture2D* texture, uint subresource, uint maxLength, uint lengthBefore, uint contiguousLength, uint length)
    {
        _taken++;
        if (lengthBefore == 0)
        {
            _takenWithZeroLength++;
        }

        string description = string.Create(CultureInfo.InvariantCulture,
            $"texture 0x{(nint)texture:X} subresource {subresource}: {MediaFoundationDiagnostics.DescribeTexture(texture)}; buffer max length {maxLength:N0}, contiguous length {contiguousLength:N0}, NV12 frame {_frameBytes:N0} bytes");
        var key = ((nint)texture, subresource);
        bool isNew = !_seen.TryGetValue(key, out string? previous);
        bool changed = !isNew && previous != description;
        _seen[key] = description;
        string lengths = string.Create(CultureInfo.InvariantCulture, $"current length {lengthBefore:N0} -> set to {length:N0}");
        _firstSample ??= $"{description}; {lengths}";
        if (_taken <= SamplesLoggedInFull || isNew || changed)
        {
            Log.Info($"GPU sample {_taken}{(isNew ? " (new texture)" : changed ? " (CHANGED)" : "")}: {description}; {lengths}");
        }

        if (length != _frameBytes && !_lengthWarned)
        {
            _lengthWarned = true;
            Log.Warn($"The GPU sample's buffer length {length:N0} differs from the NV12 frame size {_frameBytes:N0}.");
        }
    }
}
