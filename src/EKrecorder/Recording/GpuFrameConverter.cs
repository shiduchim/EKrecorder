using EKrecorder.Diagnostics;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace EKrecorder.Recording;

/// <summary>
/// Scales and converts captured frames on the GPU with the Direct3D 11 video processor: BGRA at monitor size in,
/// NV12 (BT.709, limited range) at the recording size out, in one VideoProcessorBlt per output frame. Nothing is
/// copied to the CPU. If the monitor changes resolution mid-recording, the picture is fitted into the same output
/// size with black bars.
/// </summary>
internal sealed unsafe class GpuFrameConverter : IDisposable
{
    private readonly ID3D11Device* _device;
    private readonly ID3D11DeviceContext* _context;
    private readonly ID3D11VideoDevice* _videoDevice;
    private readonly ID3D11VideoContext* _videoContext;
    private readonly int _framesPerSecond;
    private ID3D11Texture2D* _intermediate;
    private Size _intermediateSize;
    private bool _copyFirst;
    private readonly Dictionary<nint, nint> _inputViews = new();
    private readonly Dictionary<(nint Texture, uint Subresource), nint> _outputViews = new();
    private ID3D11VideoProcessorEnumerator* _enumerator;
    private ID3D11VideoProcessor* _processor;
    private Size _inputSize;

    private GpuFrameConverter(ID3D11Device* device, ID3D11DeviceContext* context, ID3D11VideoDevice* videoDevice, ID3D11VideoContext* videoContext, Size outputSize, int framesPerSecond)
    {
        device->AddRef();
        _device = device;
        _context = context;
        _videoDevice = videoDevice;
        _videoContext = videoContext;
        OutputSize = outputSize;
        _framesPerSecond = framesPerSecond;
    }

    public Size OutputSize { get; }

    public string Description { get; private set; } = "";

    /// <summary>Returns null (and logs why) when this GPU cannot do the conversion; the caller then uses the CPU path.</summary>
    public static GpuFrameConverter? TryCreate(ID3D11Device* device, Size inputSize, Size outputSize, int framesPerSecond)
    {
        ID3D11VideoDevice* videoDevice;
        HRESULT hr = device->QueryInterface(__uuidof<ID3D11VideoDevice>(), (void**)&videoDevice);
        if (hr.FAILED)
        {
            Log.Api("ID3D11Device::QueryInterface(ID3D11VideoDevice)", false, MediaFoundation.Describe(hr));
            return null;
        }

        ID3D11DeviceContext* context;
        device->GetImmediateContext(&context);
        ID3D11VideoContext* videoContext;
        hr = context->QueryInterface(__uuidof<ID3D11VideoContext>(), (void**)&videoContext);
        if (hr.FAILED)
        {
            Log.Api("ID3D11DeviceContext::QueryInterface(ID3D11VideoContext)", false, MediaFoundation.Describe(hr));
            context->Release();
            videoDevice->Release();
            return null;
        }

        var converter = new GpuFrameConverter(device, context, videoDevice, videoContext, outputSize, framesPerSecond);
        try
        {
            converter.Configure(inputSize);
            Log.Info($"Scaling: {converter.Description}");
            return converter;
        }
        catch (Exception ex)
        {
            Log.Error("The Direct3D 11 video processor cannot do this conversion", ex);
            converter.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Draws the <paramref name="contentSize"/> area of <paramref name="source"/> (BGRA) into <paramref name="target"/>
    /// (NV12). Only queues GPU work; it returns before the GPU has run it.
    /// </summary>
    public void Convert(ID3D11Texture2D* source, Size contentSize, ID3D11Texture2D* target, uint targetSubresource)
    {
        if (contentSize != _inputSize)
        {
            Log.Warn($"Captured size changed from {_inputSize.Width}x{_inputSize.Height} to {contentSize.Width}x{contentSize.Height}; reconfiguring the video processor.");
            Configure(contentSize);
        }

        ID3D11VideoProcessorInputView* input = _copyFirst ? null : TryInputViewFor(source);
        if (input == null)
        {
            // This driver cannot read the capture texture directly: copy it (on the GPU) into a texture it can read.
            input = InputViewFor(CopyToIntermediate(source, contentSize));
        }

        ID3D11VideoProcessorOutputView* output = OutputViewFor(target, targetSubresource);

        RECT sourceRect = new() { left = 0, top = 0, right = contentSize.Width, bottom = contentSize.Height };
        Rectangle fitted = FitInside(contentSize, OutputSize);
        RECT destinationRect = new() { left = fitted.Left, top = fitted.Top, right = fitted.Right, bottom = fitted.Bottom };
        _videoContext->VideoProcessorSetStreamSourceRect(_processor, 0, TRUE, &sourceRect);
        _videoContext->VideoProcessorSetStreamDestRect(_processor, 0, TRUE, &destinationRect);

        D3D11_VIDEO_PROCESSOR_STREAM stream = default;
        stream.Enable = TRUE;
        stream.pInputSurface = input;
        MediaFoundation.Check(_videoContext->VideoProcessorBlt(_processor, output, 0, 1, &stream), "ID3D11VideoContext::VideoProcessorBlt");
    }

    /// <summary>The largest rectangle with the source's aspect ratio that fits the output, centred, on even pixels.</summary>
    public static Rectangle FitInside(Size source, Size output)
    {
        double scale = Math.Min((double)output.Width / source.Width, (double)output.Height / source.Height);
        int width = Math.Min(output.Width, 2 * (int)Math.Round(source.Width * scale / 2, MidpointRounding.AwayFromZero));
        int height = Math.Min(output.Height, 2 * (int)Math.Round(source.Height * scale / 2, MidpointRounding.AwayFromZero));
        int x = ((output.Width - width) / 2) & ~1;
        int y = ((output.Height - height) / 2) & ~1;
        return new Rectangle(x, y, width, height);
    }

    /// <summary>
    /// Checks once, before recording, that the video processor can write into the encoder's textures. Throws if not,
    /// so the caller can fall back to the CPU path.
    /// </summary>
    public void CheckTarget(ID3D11Texture2D* target, uint targetSubresource) => OutputViewFor(target, targetSubresource);

    public void Dispose()
    {
        ReleaseProcessor();
        ReleaseIntermediate();
        _videoContext->Release();
        _videoDevice->Release();
        _context->Release();
        _device->Release();
    }

    private ID3D11VideoProcessorInputView* TryInputViewFor(ID3D11Texture2D* texture)
    {
        try
        {
            return InputViewFor(texture);
        }
        catch (MediaFoundationException ex)
        {
            Log.Decision($"The video processor cannot read the capture texture directly ({ex.Message}); copying each frame into an intermediate texture first.");
            _copyFirst = true;
            return null;
        }
    }

    private ID3D11Texture2D* CopyToIntermediate(ID3D11Texture2D* source, Size contentSize)
    {
        if (_intermediate == null || _intermediateSize != contentSize)
        {
            ReleaseIntermediate();
            D3D11_TEXTURE2D_DESC description = default;
            description.Width = (uint)contentSize.Width;
            description.Height = (uint)contentSize.Height;
            description.MipLevels = 1;
            description.ArraySize = 1;
            description.Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;
            description.SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 };
            description.Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT;
            description.BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET);
            ID3D11Texture2D* texture;
            MediaFoundation.Check(_device->CreateTexture2D(&description, null, &texture), "ID3D11Device::CreateTexture2D(intermediate)");
            _intermediate = texture;
            _intermediateSize = contentSize;
        }

        D3D11_BOX box = new() { left = 0, top = 0, front = 0, right = (uint)contentSize.Width, bottom = (uint)contentSize.Height, back = 1 };
        _context->CopySubresourceRegion((ID3D11Resource*)_intermediate, 0, 0, 0, 0, (ID3D11Resource*)source, 0, &box);
        return _intermediate;
    }

    private void ReleaseIntermediate()
    {
        if (_intermediate != null)
        {
            if (_inputViews.Remove((nint)_intermediate, out nint view))
            {
                ((ID3D11VideoProcessorInputView*)view)->Release();
            }

            _intermediate->Release();
            _intermediate = null;
        }
    }

    private void Configure(Size inputSize)
    {
        ReleaseProcessor();

        D3D11_VIDEO_PROCESSOR_CONTENT_DESC content = default;
        content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT.D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        content.InputFrameRate = new DXGI_RATIONAL { Numerator = (uint)_framesPerSecond, Denominator = 1 };
        content.InputWidth = (uint)inputSize.Width;
        content.InputHeight = (uint)inputSize.Height;
        content.OutputFrameRate = new DXGI_RATIONAL { Numerator = (uint)_framesPerSecond, Denominator = 1 };
        content.OutputWidth = (uint)OutputSize.Width;
        content.OutputHeight = (uint)OutputSize.Height;
        content.Usage = D3D11_VIDEO_USAGE.D3D11_VIDEO_USAGE_OPTIMAL_SPEED;

        ID3D11VideoProcessorEnumerator* enumerator;
        MediaFoundation.Check(_videoDevice->CreateVideoProcessorEnumerator(&content, &enumerator), "ID3D11VideoDevice::CreateVideoProcessorEnumerator");
        _enumerator = enumerator;

        uint inputSupport;
        uint outputSupport;
        MediaFoundation.Check(enumerator->CheckVideoProcessorFormat(DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, &inputSupport), "CheckVideoProcessorFormat(BGRA)");
        MediaFoundation.Check(enumerator->CheckVideoProcessorFormat(DXGI_FORMAT.DXGI_FORMAT_NV12, &outputSupport), "CheckVideoProcessorFormat(NV12)");
        if ((inputSupport & (uint)D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT.D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_INPUT) == 0
            || (outputSupport & (uint)D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT.D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_OUTPUT) == 0)
        {
            throw new NotSupportedException($"The video processor cannot convert BGRA to NV12 (input support 0x{inputSupport:X}, output support 0x{outputSupport:X}).");
        }

        ID3D11VideoProcessor* processor;
        MediaFoundation.Check(_videoDevice->CreateVideoProcessor(enumerator, 0, &processor), "ID3D11VideoDevice::CreateVideoProcessor");
        _processor = processor;

        // Colour: the desktop is full-range sRGB; the video is BT.709 limited range, which every player expects.
        string colour;
        ID3D11VideoContext1* videoContext1;
        if (_videoContext->QueryInterface(__uuidof<ID3D11VideoContext1>(), (void**)&videoContext1).SUCCEEDED)
        {
            videoContext1->VideoProcessorSetStreamColorSpace1(processor, 0, DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709);
            videoContext1->VideoProcessorSetOutputColorSpace1(processor, DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_YCBCR_STUDIO_G22_LEFT_P709);
            videoContext1->Release();
            colour = "colour space RGB full range -> YCbCr BT.709 studio range (ID3D11VideoContext1)";
        }
        else
        {
            D3D11_VIDEO_PROCESSOR_COLOR_SPACE inputSpace = default;
            inputSpace.RGB_Range = 0;    // 0-255
            inputSpace.YCbCr_Matrix = 1; // BT.709
            inputSpace.Nominal_Range = (uint)D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE.D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_0_255;
            D3D11_VIDEO_PROCESSOR_COLOR_SPACE outputSpace = default;
            outputSpace.RGB_Range = 0;
            outputSpace.YCbCr_Matrix = 1;
            outputSpace.Nominal_Range = (uint)D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE.D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_16_235;
            _videoContext->VideoProcessorSetStreamColorSpace(processor, 0, &inputSpace);
            _videoContext->VideoProcessorSetOutputColorSpace(processor, &outputSpace);
            colour = "colour space BT.709, 0-255 -> 16-235 (ID3D11VideoContext)";
        }

        _videoContext->VideoProcessorSetStreamFrameFormat(processor, 0, D3D11_VIDEO_FRAME_FORMAT.D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
        // No driver "enhancements" (sharpening, colour tweaks): the recording should look like the screen.
        _videoContext->VideoProcessorSetStreamAutoProcessingMode(processor, 0, FALSE);
        RECT target = new() { left = 0, top = 0, right = OutputSize.Width, bottom = OutputSize.Height };
        _videoContext->VideoProcessorSetOutputTargetRect(processor, TRUE, &target);
        D3D11_VIDEO_COLOR black = default;
        black.RGBA.R = 0;
        black.RGBA.G = 0;
        black.RGBA.B = 0;
        black.RGBA.A = 1;
        _videoContext->VideoProcessorSetOutputBackgroundColor(processor, FALSE, &black);

        _inputSize = inputSize;
        Description = $"GPU, Direct3D 11 video processor: {inputSize.Width}x{inputSize.Height} BGRA -> {OutputSize.Width}x{OutputSize.Height} NV12, {colour}";
    }

    private ID3D11VideoProcessorInputView* InputViewFor(ID3D11Texture2D* texture)
    {
        if (_inputViews.TryGetValue((nint)texture, out nint cached))
        {
            return (ID3D11VideoProcessorInputView*)cached;
        }

        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC description = default;
        description.FourCC = 0;
        description.ViewDimension = D3D11_VPIV_DIMENSION.D3D11_VPIV_DIMENSION_TEXTURE2D;
        description.Texture2D.MipSlice = 0;
        description.Texture2D.ArraySlice = 0;
        ID3D11VideoProcessorInputView* view;
        MediaFoundation.Check(
            _videoDevice->CreateVideoProcessorInputView((ID3D11Resource*)texture, _enumerator, &description, &view),
            "ID3D11VideoDevice::CreateVideoProcessorInputView");
        _inputViews[(nint)texture] = (nint)view;
        return view;
    }

    private ID3D11VideoProcessorOutputView* OutputViewFor(ID3D11Texture2D* texture, uint subresource)
    {
        if (_outputViews.TryGetValue(((nint)texture, subresource), out nint cached))
        {
            return (ID3D11VideoProcessorOutputView*)cached;
        }

        D3D11_TEXTURE2D_DESC textureDescription;
        texture->GetDesc(&textureDescription);
        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC description = default;
        if (textureDescription.ArraySize > 1)
        {
            // The sample allocator may hand out slices of one texture array.
            description.ViewDimension = D3D11_VPOV_DIMENSION.D3D11_VPOV_DIMENSION_TEXTURE2DARRAY;
            description.Texture2DArray.MipSlice = 0;
            description.Texture2DArray.FirstArraySlice = subresource;
            description.Texture2DArray.ArraySize = 1;
        }
        else
        {
            description.ViewDimension = D3D11_VPOV_DIMENSION.D3D11_VPOV_DIMENSION_TEXTURE2D;
            description.Texture2D.MipSlice = 0;
        }

        ID3D11VideoProcessorOutputView* view;
        MediaFoundation.Check(
            _videoDevice->CreateVideoProcessorOutputView((ID3D11Resource*)texture, _enumerator, &description, &view),
            "ID3D11VideoDevice::CreateVideoProcessorOutputView");
        _outputViews[((nint)texture, subresource)] = (nint)view;
        return view;
    }

    private void ReleaseProcessor()
    {
        foreach (nint view in _inputViews.Values)
        {
            ((ID3D11VideoProcessorInputView*)view)->Release();
        }

        foreach (nint view in _outputViews.Values)
        {
            ((ID3D11VideoProcessorOutputView*)view)->Release();
        }

        _inputViews.Clear();
        _outputViews.Clear();
        if (_processor != null)
        {
            _processor->Release();
            _processor = null;
        }

        if (_enumerator != null)
        {
            _enumerator->Release();
            _enumerator = null;
        }
    }
}
