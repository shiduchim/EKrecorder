using EKrecorder.Diagnostics;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace EKrecorder.Recording;

/// <summary>
/// Fallback when the GPU has no usable Direct3D 11 video processor: copies the captured frame to a CPU-readable
/// texture, then scales it (bilinear) and converts it to NV12 (BT.709, limited range) on the CPU. Slower and heavier
/// than <see cref="GpuFrameConverter"/>; only used when that one is not available.
/// </summary>
internal sealed unsafe class CpuFrameConverter : IDisposable
{
    private readonly ID3D11Device* _device;
    private readonly ID3D11DeviceContext* _context;
    private readonly byte[] _nv12;
    private ID3D11Texture2D* _staging;
    private Size _stagingSize;
    private Size _copiedSize;
    private (Size Source, Rectangle Target) _columnsFor;
    private int[] _x0 = [];
    private int[] _x1 = [];
    private int[] _wx = [];

    public CpuFrameConverter(ID3D11Device* device, Size outputSize)
    {
        device->AddRef();
        _device = device;
        ID3D11DeviceContext* context;
        device->GetImmediateContext(&context);
        _context = context;
        OutputSize = outputSize;
        _nv12 = new byte[outputSize.Width * outputSize.Height * 3 / 2];
        Description = $"CPU fallback: frame copied to system memory, scaled (bilinear) and converted to {outputSize.Width}x{outputSize.Height} NV12, BT.709 limited range";
        Log.Info($"Scaling: {Description}");
    }

    public Size OutputSize { get; }

    public string Description { get; }

    /// <summary>The most recent converted frame (NV12: Y plane, then interleaved UV).</summary>
    public ReadOnlySpan<byte> LastFrame => _nv12;

    /// <summary>Queues a GPU copy of the frame's content into the CPU-readable texture. Call while the frame is held.</summary>
    public void CopyFrom(ID3D11Texture2D* source, Size contentSize)
    {
        if (_staging == null || _stagingSize != contentSize)
        {
            CreateStaging(contentSize);
        }

        D3D11_BOX box = new() { left = 0, top = 0, front = 0, right = (uint)contentSize.Width, bottom = (uint)contentSize.Height, back = 1 };
        _context->CopySubresourceRegion((ID3D11Resource*)_staging, 0, 0, 0, 0, (ID3D11Resource*)source, 0, &box);
        _copiedSize = contentSize;
    }

    /// <summary>Waits for the copy, then scales and converts it into <see cref="LastFrame"/>.</summary>
    public void Convert()
    {
        D3D11_MAPPED_SUBRESOURCE mapped;
        MediaFoundation.Check(_context->Map((ID3D11Resource*)_staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "ID3D11DeviceContext::Map");
        try
        {
            Rectangle fitted = GpuFrameConverter.FitInside(_copiedSize, OutputSize);
            if (fitted.Size != OutputSize)
            {
                FillBlack();
            }

            ScaleToNv12((byte*)mapped.pData, (int)mapped.RowPitch, _copiedSize, fitted);
        }
        finally
        {
            _context->Unmap((ID3D11Resource*)_staging, 0);
        }
    }

    public void Dispose()
    {
        if (_staging != null)
        {
            _staging->Release();
            _staging = null;
        }

        _context->Release();
        _device->Release();
    }

    private void CreateStaging(Size size)
    {
        if (_staging != null)
        {
            _staging->Release();
            _staging = null;
        }

        D3D11_TEXTURE2D_DESC description = default;
        description.Width = (uint)size.Width;
        description.Height = (uint)size.Height;
        description.MipLevels = 1;
        description.ArraySize = 1;
        description.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
        description.SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 };
        description.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
        description.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
        ID3D11Texture2D* staging;
        MediaFoundation.Check(_device->CreateTexture2D(&description, null, &staging), "ID3D11Device::CreateTexture2D(staging)");
        _staging = staging;
        _stagingSize = size;
    }

    private void FillBlack()
    {
        int luma = OutputSize.Width * OutputSize.Height;
        _nv12.AsSpan(0, luma).Fill(16);
        _nv12.AsSpan(luma).Fill(128);
    }

    /// <summary>
    /// Bilinear scaling of BGRA <paramref name="source"/> into <paramref name="target"/> of the NV12 output, with
    /// BT.709 limited-range coefficients in 8.8 fixed point. Chroma is the average of each 2×2 block.
    /// </summary>
    private void ScaleToNv12(byte* source, int pitch, Size sourceSize, Rectangle target)
    {
        if (_columnsFor != (sourceSize, target))
        {
            // Horizontal sample positions only change when the sizes change.
            _x0 = new int[target.Width];
            _x1 = new int[target.Width];
            _wx = new int[target.Width];
            for (int i = 0; i < target.Width; i++)
            {
                double sx = Math.Clamp(((i + 0.5) * sourceSize.Width / target.Width) - 0.5, 0, sourceSize.Width - 1);
                _x0[i] = (int)sx;
                _x1[i] = Math.Min(_x0[i] + 1, sourceSize.Width - 1);
                _wx[i] = (int)((sx - _x0[i]) * 256);
            }

            _columnsFor = (sourceSize, target);
        }

        int outWidth = OutputSize.Width;
        int lumaSize = outWidth * OutputSize.Height;
        int width = target.Width;
        int[] x0 = _x0;
        int[] x1 = _x1;
        int[] wx = _wx;
        nint sourceAddress = (nint)source;
        byte[] nv12 = _nv12;

        // Two output rows per work item (one chroma row); each worker keeps one scratch buffer.
        Parallel.For(
            0,
            target.Height / 2,
            () => new int[6 * width],
            (pair, _, scratch) =>
            {
                for (int k = 0; k < 2; k++)
                {
                    int j = (2 * pair) + k;
                    double sy = Math.Clamp(((j + 0.5) * sourceSize.Height / target.Height) - 0.5, 0, sourceSize.Height - 1);
                    int y0 = (int)sy;
                    int y1 = Math.Min(y0 + 1, sourceSize.Height - 1);
                    int wy = (int)((sy - y0) * 256);
                    byte* row0 = (byte*)sourceAddress + ((long)y0 * pitch);
                    byte* row1 = (byte*)sourceAddress + ((long)y1 * pitch);
                    int lumaRow = ((target.Y + j) * outWidth) + target.X;
                    int rgbRow = k * 3 * width;
                    for (int i = 0; i < width; i++)
                    {
                        byte* a = row0 + (x0[i] * 4);
                        byte* b = row0 + (x1[i] * 4);
                        byte* c = row1 + (x0[i] * 4);
                        byte* d = row1 + (x1[i] * 4);
                        int fx = wx[i];
                        int bl = Mix(a[0], b[0], c[0], d[0], fx, wy);
                        int gr = Mix(a[1], b[1], c[1], d[1], fx, wy);
                        int rd = Mix(a[2], b[2], c[2], d[2], fx, wy);
                        scratch[rgbRow + (3 * i)] = rd;
                        scratch[rgbRow + (3 * i) + 1] = gr;
                        scratch[rgbRow + (3 * i) + 2] = bl;
                        nv12[lumaRow + i] = (byte)(16 + (((47 * rd) + (157 * gr) + (16 * bl) + 128) >> 8));
                    }
                }

                int chromaRow = lumaSize + ((((target.Y / 2) + pair) * outWidth) + target.X);
                int below = 3 * width;
                for (int i = 0; i + 1 < width; i += 2)
                {
                    int p = 3 * i;
                    int rd = (scratch[p] + scratch[p + 3] + scratch[below + p] + scratch[below + p + 3] + 2) >> 2;
                    int gr = (scratch[p + 1] + scratch[p + 4] + scratch[below + p + 1] + scratch[below + p + 4] + 2) >> 2;
                    int bl = (scratch[p + 2] + scratch[p + 5] + scratch[below + p + 2] + scratch[below + p + 5] + 2) >> 2;
                    nv12[chromaRow + i] = (byte)Math.Clamp(128 + (((-26 * rd) - (87 * gr) + (112 * bl) + 128) >> 8), 16, 240);
                    nv12[chromaRow + i + 1] = (byte)Math.Clamp(128 + (((112 * rd) - (102 * gr) - (10 * bl) + 128) >> 8), 16, 240);
                }

                return scratch;
            },
            _ => { });
    }

    private static int Mix(int a, int b, int c, int d, int fx, int fy)
    {
        int top = (a * (256 - fx)) + (b * fx);
        int bottom = (c * (256 - fx)) + (d * fx);
        return ((top * (256 - fy)) + (bottom * fy) + 32768) >> 16;
    }
}
