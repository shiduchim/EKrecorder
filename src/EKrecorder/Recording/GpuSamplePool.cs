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
/// </summary>
internal sealed unsafe class GpuSamplePool : IDisposable
{
    private const uint InitialSamples = 4;
    private const uint MaximumSamples = 10;
    private const int MF_E_SAMPLEALLOCATOR_EMPTY = unchecked((int)0xC00D4A3E);

    private IMFVideoSampleAllocatorEx* _allocator;

    private GpuSamplePool(IMFVideoSampleAllocatorEx* allocator) => _allocator = allocator;

    public static GpuSamplePool? TryCreate(IMFDXGIDeviceManager* manager, Size size, int framesPerSecond)
    {
        IMFVideoSampleAllocatorEx* allocator = null;
        IMFAttributes* attributes = null;
        IMFMediaType* type = null;
        try
        {
            Check(MFCreateVideoSampleAllocatorEx(__uuidof<IMFVideoSampleAllocatorEx>(), (void**)&allocator), "MFCreateVideoSampleAllocatorEx");
            Check(allocator->SetDirectXManager((IUnknown*)manager), "IMFVideoSampleAllocatorEx::SetDirectXManager");
            Check(MFCreateAttributes(&attributes, 2), "MFCreateAttributes");
            // Render-target textures: the video processor writes into them.
            Check(attributes->SetUINT32(Ptr(in MF.MF_SA_D3D11_BINDFLAGS), (uint)D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET), "set MF_SA_D3D11_BINDFLAGS");
            Check(attributes->SetUINT32(Ptr(in MF.MF_SA_D3D11_USAGE), (uint)D3D11_USAGE.D3D11_USAGE_DEFAULT), "set MF_SA_D3D11_USAGE");
            type = CreateVideoType(in MFVideoFormat.MFVideoFormat_NV12, size.Width, size.Height, framesPerSecond);
            Check(allocator->InitializeSampleAllocatorEx(InitialSamples, MaximumSamples, attributes, type), "IMFVideoSampleAllocatorEx::InitializeSampleAllocatorEx");
            Log.Info($"GPU sample pool: {InitialSamples} to {MaximumSamples} NV12 {size.Width}x{size.Height} textures");
            var pool = new GpuSamplePool(allocator);
            allocator = null;
            return pool;
        }
        catch (MediaFoundationException ex)
        {
            Log.Error("Could not create GPU video samples", ex);
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
    /// A free sample and its texture (both AddRef'ed; release both). False when every sample is still with the encoder.
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
        try
        {
            Check(taken->GetBufferByIndex(0, &buffer), "IMFSample::GetBufferByIndex");
            Check(buffer->QueryInterface(__uuidof<IMFDXGIBuffer>(), (void**)&dxgiBuffer), "IMFMediaBuffer::QueryInterface(IMFDXGIBuffer)");
            ID3D11Texture2D* resource;
            Check(dxgiBuffer->GetResource(__uuidof<ID3D11Texture2D>(), (void**)&resource), "IMFDXGIBuffer::GetResource");
            uint index;
            hr = dxgiBuffer->GetSubresourceIndex(&index);
            if (hr.FAILED)
            {
                resource->Release();
                Check(hr, "IMFDXGIBuffer::GetSubresourceIndex");
            }

            sample = taken;
            texture = resource;
            subresource = index;
            taken = null;
            return true;
        }
        finally
        {
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
}
