using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace VeilDesk.App.Capture;

/// <summary>
/// Minimal, self-contained WGC/D3D11 interop. We intentionally avoid a large
/// third-party graphics dependency: only the handful of COM methods needed to
/// create a D3D11 device and read a captured texture are declared here.
/// </summary>
internal static class WgcInterop
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid CaptureInteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(IntPtr window, in Guid iid, out IntPtr result);

        [PreserveSig]
        int CreateForMonitor(IntPtr monitor, in Guid iid, out IntPtr result);
    }

    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr monitor)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        int hr = WindowsCreateString(className, className.Length, out var hstring);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            hr = RoGetActivationFactory(hstring, in CaptureInteropIid, out var factoryPtr);
            Marshal.ThrowExceptionForHR(hr);
            try
            {
                var factory = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
                var iid = GraphicsCaptureItemIid;
                hr = factory.CreateForMonitor(monitor, in iid, out var itemPtr);
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    return MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
                }
                finally
                {
                    Marshal.Release(itemPtr);
                }
            }
            finally
            {
                Marshal.Release(factoryPtr);
            }
        }
        finally
        {
            _ = WindowsDeleteString(hstring);
        }
    }
}

internal sealed unsafe class D3D11Context : IDisposable
{
    private const uint BgraSupport = 0x20;
    private const uint SdkVersion = 7;
    private const int DriverTypeHardware = 1;
    private const int DriverTypeWarp = 5;
    private const uint StagingUsage = 3;
    private const uint CpuAccessRead = 0x10000;
    private const uint MapRead = 2;

    private nint _device;
    private nint _context;

    private D3D11Context(nint device, nint context, int featureLevel)
    {
        _device = device;
        _context = context;
        FeatureLevel = featureLevel;
    }

    public nint Device => _device;
    public nint Context => _context;
    public int FeatureLevel { get; }
    public IDirect3DDevice? WinRtDevice { get; private set; }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        nint adapter, int driverType, nint software, uint flags,
        int* featureLevels, uint featureLevelCount, uint sdkVersion,
        out nint device, out int featureLevel, out nint immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    public static D3D11Context Create()
    {
        int hr = D3D11CreateDevice(0, DriverTypeHardware, 0, BgraSupport, null, 0, SdkVersion,
            out var device, out var featureLevel, out var context);
        if (hr < 0)
        {
            hr = D3D11CreateDevice(0, DriverTypeWarp, 0, BgraSupport, null, 0, SdkVersion,
                out device, out featureLevel, out context);
        }
        Marshal.ThrowExceptionForHR(hr);
        var result = new D3D11Context(device, context, featureLevel);
        var iidDxgiDevice = new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
        hr = Marshal.QueryInterface(device, in iidDxgiDevice, out var dxgiDevice);
        if (hr < 0)
        {
            result.Dispose();
            Marshal.ThrowExceptionForHR(hr);
        }
        try
        {
            hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable);
            Marshal.ThrowExceptionForHR(hr);
            result.WinRtDevice = MarshalInspectable<IDirect3DDevice>.FromAbi(inspectable);
            return result;
        }
        finally
        {
            Marshal.Release(dxgiDevice);
        }
    }

    public TextureDescription Describe(IDirect3DSurface surface)
    {
        nint surfacePtr = MarshalInspectable<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var texturePtr = GetTextureFromSurface(surfacePtr);
            try
            {
                Texture2DDescription desc;
                var getDesc = (delegate* unmanaged[Stdcall]<nint, Texture2DDescription*, void>)((*(nint**)texturePtr)[10]);
                getDesc(texturePtr, &desc);
                return new TextureDescription((int)desc.Width, (int)desc.Height, (int)desc.Format);
            }
            finally
            {
                Release(texturePtr);
            }
        }
        finally
        {
            Marshal.Release(surfacePtr);
        }
    }

    public nint CreateStaging(TextureDescription desc)
    {
        var textureDesc = new Texture2DDescription
        {
            Width = (uint)desc.Width,
            Height = (uint)desc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = desc.Format,
            SampleCount = 1,
            SampleQuality = 0,
            Usage = StagingUsage,
            BindFlags = 0,
            CpuAccessFlags = CpuAccessRead,
            MiscFlags = 0,
        };
        var createTexture2D = (delegate* unmanaged[Stdcall]<nint, Texture2DDescription*, nint, nint*, int>)((*(nint**)_device)[5]);
        nint texture = 0;
        int hr = createTexture2D(_device, &textureDesc, 0, &texture);
        Marshal.ThrowExceptionForHR(hr);
        return texture;
    }

    /// <summary>Copies the WGC surface into a staging texture and reads rows into a packed BGRA buffer.</summary>
    public void CopyTo(byte[] destination, int destinationStride, int width, int height,
        IDirect3DSurface surface, nint staging)
    {
        nint surfacePtr = MarshalInspectable<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var sourceTexture = GetTextureFromSurface(surfacePtr);
            try
            {
                var copyResource = (delegate* unmanaged[Stdcall]<nint, nint, nint, void>)((*(nint**)_context)[47]);
                copyResource(_context, staging, sourceTexture);
            }
            finally
            {
                Release(sourceTexture);
            }
        }
        finally
        {
            Marshal.Release(surfacePtr);
        }

        MappedSubresource mapped;
        var map = (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, MappedSubresource*, int>)((*(nint**)_context)[14]);
        int hr = map(_context, staging, 0, MapRead, 0, &mapped);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            fixed (byte* dstBase = destination)
            {
                for (var y = 0; y < height; y++)
                {
                    var src = (byte*)mapped.Data + (nint)y * mapped.RowPitch;
                    var dst = dstBase + (nint)y * destinationStride;
                    Buffer.MemoryCopy(src, dst, destinationStride, width * 4);
                }
            }
        }
        finally
        {
            var unmap = (delegate* unmanaged[Stdcall]<nint, nint, uint, void>)((*(nint**)_context)[15]);
            unmap(_context, staging, 0);
        }
    }

    private static nint GetTextureFromSurface(nint surfacePtr)
    {
        var iidAccess = new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
        int hr = Marshal.QueryInterface(surfacePtr, in iidAccess, out var accessPtr);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            var iidTexture = new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
            var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)((*(nint**)accessPtr)[3]);
            nint texturePtr = 0;
            hr = getInterface(accessPtr, &iidTexture, &texturePtr);
            Marshal.ThrowExceptionForHR(hr);
            return texturePtr;
        }
        finally
        {
            Marshal.Release(accessPtr);
        }
    }

    private static void Release(nint p)
    {
        if (p == 0) return;
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)((*(nint**)p)[2]);
        release(p);
    }

    public void ReleaseTexture(nint texture) => Release(texture);

    public void Dispose()
    {
        if (_context != 0)
        {
            Release(_context);
            _context = 0;
        }
        if (_device != 0)
        {
            Release(_device);
            _device = 0;
        }
        // WinRtDevice is a managed projection. The capture service keeps one
        // D3D11Context alive across Start/Stop, so any projection lifetime
        // is bounded by that single instance.
        WinRtDevice = null;
    }
}

internal readonly record struct TextureDescription(int Width, int Height, int Format);

[StructLayout(LayoutKind.Sequential)]
internal struct Texture2DDescription
{
    public uint Width;
    public uint Height;
    public uint MipLevels;
    public uint ArraySize;
    public int Format;
    public uint SampleCount;
    public uint SampleQuality;
    public uint Usage;
    public uint BindFlags;
    public uint CpuAccessFlags;
    public uint MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MappedSubresource
{
    public nint Data;
    public uint RowPitch;
    public uint DepthPitch;
}
