using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace WgcProbe;

internal sealed unsafe class D3D11Context : IDisposable
{
    private const uint BgraSupport = 0x20;
    private const uint SdkVersion = 7;
    private const int DriverTypeHardware = 1;
    private const int DriverTypeWarp = 5;
    private const uint StagingUsage = 3;
    private const uint CpuAccessRead = 0x10000;
    private const uint MapRead = 2;
    private const int Bgra8Unorm = 87;

    private D3D11Context(nint device, nint context, int featureLevel)
    {
        Device = device;
        Context = context;
        FeatureLevel = featureLevel;
    }

    public nint Device { get; }

    public nint Context { get; }

    public int FeatureLevel { get; }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        nint adapter,
        int driverType,
        nint software,
        uint flags,
        int* featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out nint device,
        out int featureLevel,
        out nint immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    public static D3D11Context Create()
    {
        int hr = D3D11CreateDevice(0, DriverTypeHardware, 0, BgraSupport, null, 0, SdkVersion,
            out var device, out var featureLevel, out var context);
        if (hr < 0)
        {
            Console.WriteLine($"hardware D3D11CreateDevice failed 0x{hr:X8}; trying WARP");
            hr = D3D11CreateDevice(0, DriverTypeWarp, 0, BgraSupport, null, 0, SdkVersion,
                out device, out featureLevel, out context);
        }

        Marshal.ThrowExceptionForHR(hr);
        return new D3D11Context(device, context, featureLevel);
    }

    public nint CreateWinRtDevice()
    {
        var iidDxgiDevice = new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
        int hr = Marshal.QueryInterface(Device, in iidDxgiDevice, out var dxgiDevice);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable);
            Marshal.ThrowExceptionForHR(hr);
            return inspectable;
        }
        finally
        {
            Marshal.Release(dxgiDevice);
        }
    }

    public TextureDescription Describe(IDirect3DSurface surface)
    {
        nint surfacePtr = MarshalSurface(surface);
        try
        {
            var texturePtr = GetTextureFromSurface(surfacePtr);
            try
            {
                return GetDescription(texturePtr);
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

    public StagingTexture CreateStagingTexture(TextureDescription desc)
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

        var createTexture2D = (delegate* unmanaged[Stdcall]<nint, Texture2DDescription*, nint, nint*, int>)
            ((*(nint**)Device)[5]);
        nint texture = 0;
        int hr = createTexture2D(Device, &textureDesc, 0, &texture);
        Marshal.ThrowExceptionForHR(hr);
        return new StagingTexture(texture, desc);
    }

    public void CopyToStaging(IDirect3DSurface surface, nint stagingTexture)
    {
        nint surfacePtr = MarshalSurface(surface);
        try
        {
            var sourceTexture = GetTextureFromSurface(surfacePtr);
            try
            {
                var copyResource = (delegate* unmanaged[Stdcall]<nint, nint, nint, void>)((*(nint**)Context)[47]);
                copyResource(Context, stagingTexture, sourceTexture);
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
    }

    public byte[] ReadPixels(StagingTexture staging, out int rowPitch, out int width, out int height)
    {
        width = staging.Description.Width;
        height = staging.Description.Height;
        MappedSubresource mapped;
        var map = (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, MappedSubresource*, int>)
            ((*(nint**)Context)[14]);
        int hr = map(Context, staging.Texture, 0, MapRead, 0, &mapped);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            rowPitch = (int)mapped.RowPitch;
            var result = new byte[rowPitch * height];
            fixed (byte* dst = result)
            {
                Buffer.MemoryCopy((void*)mapped.Data, dst, result.Length, result.Length);
            }
            return result;
        }
        finally
        {
            var unmap = (delegate* unmanaged[Stdcall]<nint, nint, uint, void>)((*(nint**)Context)[15]);
            unmap(Context, staging.Texture, 0);
        }
    }

    private static nint MarshalSurface(IDirect3DSurface surface)
    {
        return MarshalInspectable<IDirect3DSurface>.FromManaged(surface);
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

    private static TextureDescription GetDescription(nint texture)
    {
        Texture2DDescription desc;
        var getDesc = (delegate* unmanaged[Stdcall]<nint, Texture2DDescription*, void>)((*(nint**)texture)[10]);
        getDesc(texture, &desc);
        return new TextureDescription((int)desc.Width, (int)desc.Height, (int)desc.Format);
    }

    private static void Release(nint p)
    {
        if (p != 0)
        {
            var release = (delegate* unmanaged[Stdcall]<nint, uint>)((*(nint**)p)[2]);
            release(p);
        }
    }

    public void Dispose()
    {
        Release(Context);
        Release(Device);
    }
}

internal readonly record struct TextureDescription(int Width, int Height, int Format);

internal sealed unsafe class StagingTexture : IDisposable
{
    public StagingTexture(nint texture, TextureDescription description)
    {
        Texture = texture;
        Description = description;
    }

    public nint Texture { get; }

    public TextureDescription Description { get; }

    public void Dispose()
    {
        if (Texture != 0)
        {
            var release = (delegate* unmanaged[Stdcall]<nint, uint>)((*(nint**)Texture)[2]);
            release(Texture);
        }
    }
}

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
