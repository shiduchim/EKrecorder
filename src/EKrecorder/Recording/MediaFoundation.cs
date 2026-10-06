using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EKrecorder.Diagnostics;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace EKrecorder.Recording;

/// <summary>Media Foundation start-up, HRESULT checks and the small helpers the recording code shares.</summary>
internal static unsafe class MediaFoundation
{
    private const uint MF_VERSION = 0x00020070; // MF_SDK_VERSION 0x0002, MF_API_VERSION 0x0070
    private const uint MFSTARTUP_NOSOCKET = 0x1;

    private static readonly object Gate = new();
    private static bool _started;

    public static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_started)
            {
                return;
            }

            Check(MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET), "MFStartup");
            _started = true;
            Log.Info("Media Foundation started.");
        }
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            if (_started)
            {
                MFShutdown();
                _started = false;
            }
        }
    }

    public static void Check(HRESULT hr, string operation)
    {
        if (hr.FAILED)
        {
            throw new MediaFoundationException(operation, hr);
        }
    }

    /// <summary>
    /// The address of a TerraFX GUID constant. Those constants live in the assembly's read-only data, so the address
    /// stays valid; this is how a <c>REFGUID</c> argument is passed.
    /// </summary>
    public static Guid* Ptr(in Guid guid) => (Guid*)Unsafe.AsPointer(ref Unsafe.AsRef(in guid));

    public static string Describe(int hr)
    {
        string? message = Marshal.GetExceptionForHR(hr)?.Message?.Trim();
        return string.IsNullOrEmpty(message) || message.StartsWith("Exception from HRESULT", StringComparison.Ordinal)
            ? $"HRESULT 0x{hr:X8}"
            : $"HRESULT 0x{hr:X8} ({message})";
    }

    /// <summary>A video media type: progressive, square pixels, BT.709 colour, limited (16-235) range.</summary>
    public static IMFMediaType* CreateVideoType(in Guid subtype, int width, int height, int framesPerSecond)
    {
        IMFMediaType* type;
        Check(MFCreateMediaType(&type), "MFCreateMediaType");
        try
        {
            Check(type->SetGUID(Ptr(in MF.MF_MT_MAJOR_TYPE), Ptr(in MFMediaType_Video)), "set MF_MT_MAJOR_TYPE");
            Check(type->SetGUID(Ptr(in MF.MF_MT_SUBTYPE), Ptr(in subtype)), "set MF_MT_SUBTYPE");
            Check(MFSetAttributeSize((IMFAttributes*)type, Ptr(in MF.MF_MT_FRAME_SIZE), (uint)width, (uint)height), "set MF_MT_FRAME_SIZE");
            Check(MFSetAttributeRatio((IMFAttributes*)type, Ptr(in MF.MF_MT_FRAME_RATE), (uint)framesPerSecond, 1), "set MF_MT_FRAME_RATE");
            Check(MFSetAttributeRatio((IMFAttributes*)type, Ptr(in MF.MF_MT_PIXEL_ASPECT_RATIO), 1, 1), "set MF_MT_PIXEL_ASPECT_RATIO");
            Check(type->SetUINT32(Ptr(in MF.MF_MT_INTERLACE_MODE), (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive), "set MF_MT_INTERLACE_MODE");
            Check(type->SetUINT32(Ptr(in MF.MF_MT_VIDEO_PRIMARIES), (uint)MFVideoPrimaries.MFVideoPrimaries_BT709), "set MF_MT_VIDEO_PRIMARIES");
            Check(type->SetUINT32(Ptr(in MF.MF_MT_TRANSFER_FUNCTION), (uint)MFVideoTransferFunction.MFVideoTransFunc_709), "set MF_MT_TRANSFER_FUNCTION");
            Check(type->SetUINT32(Ptr(in MF.MF_MT_YUV_MATRIX), (uint)MFVideoTransferMatrix.MFVideoTransferMatrix_BT709), "set MF_MT_YUV_MATRIX");
            Check(type->SetUINT32(Ptr(in MF.MF_MT_VIDEO_NOMINAL_RANGE), (uint)MFNominalRange.MFNominalRange_16_235), "set MF_MT_VIDEO_NOMINAL_RANGE");
            return type;
        }
        catch
        {
            type->Release();
            throw;
        }
    }

    /// <summary>Reads a string attribute, or null when it is not set.</summary>
    public static string? GetString(IMFAttributes* attributes, in Guid key)
    {
        char* value = null;
        uint length;
        if (attributes->GetAllocatedString(Ptr(in key), &value, &length).FAILED || value == null)
        {
            return null;
        }

        try
        {
            return new string(value, 0, (int)length);
        }
        finally
        {
            CoTaskMemFree(value);
        }
    }

    public static uint? GetUInt32(IMFAttributes* attributes, in Guid key)
    {
        uint value;
        return attributes->GetUINT32(Ptr(in key), &value).SUCCEEDED ? value : null;
    }

    /// <summary>Maps a PCI vendor id ("VEN_10DE", or the number) to a vendor name.</summary>
    public static string? VendorName(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        string upper = text.ToUpperInvariant();
        if (upper.Contains("10DE", StringComparison.Ordinal))
        {
            return "NVIDIA";
        }

        if (upper.Contains("8086", StringComparison.Ordinal))
        {
            return "Intel";
        }

        if (upper.Contains("1002", StringComparison.Ordinal) || upper.Contains("1022", StringComparison.Ordinal))
        {
            return "AMD";
        }

        if (upper.Contains("5143", StringComparison.Ordinal) || upper.Contains("4D4F4351", StringComparison.Ordinal) || upper.Contains("QCOM", StringComparison.Ordinal))
        {
            return "Qualcomm";
        }

        return null;
    }
}

/// <summary>A failed Media Foundation or Direct3D call, with the operation that failed.</summary>
internal sealed class MediaFoundationException : Exception
{
    public MediaFoundationException(string operation, int hr)
        : base($"{operation} failed: {MediaFoundation.Describe(hr)}")
    {
        Operation = operation;
        HResult = hr;
    }

    public string Operation { get; }
}
