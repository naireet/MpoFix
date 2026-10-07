using System.Runtime.InteropServices;

namespace MpoFix.Interop;

// ABI: https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_mode_info
// The largest union member is the 48-byte DISPLAYCONFIG_VIDEO_SIGNAL_INFO (target mode).
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct CcdMode
{
    [FieldOffset(0)] public uint Type;
    [FieldOffset(4)] public uint Id;
    [FieldOffset(8)] public LUID Adapter;
    [FieldOffset(16)] public CcdSourceMode Source;
    [FieldOffset(16)] public CcdSignal Target;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CcdSourceMode
{
    public uint Width, Height, PixelFormat;
    public int X, Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CcdSignal
{
    public ulong PixelRate;
    public CcdRational HSync, VSync;
    public uint ActiveWidth, ActiveHeight, TotalWidth, TotalHeight, VideoStandard, ScanLineOrdering;
}

[StructLayout(LayoutKind.Sequential)]
internal record struct CcdRational(uint Numerator, uint Denominator);

[StructLayout(LayoutKind.Sequential)]
internal struct CcdSource
{
    public LUID Adapter;
    public uint Id, ModeIndex, StatusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CcdTarget
{
    public LUID Adapter;
    public uint Id, ModeIndex, OutputTechnology, Rotation, Scaling;
    public CcdRational Refresh;
    public uint ScanLineOrdering;
    public int Available;
    public uint StatusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CcdPath
{
    public CcdSource Source;
    public CcdTarget Target;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CcdHeader
{
    public uint Type, Size;
    public LUID Adapter;
    public uint Id;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct CcdSourceName
{
    public CcdHeader Header;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string GdiName;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct CcdTargetName
{
    public CcdHeader Header;
    public uint Flags, OutputTechnology;
    public ushort Manufacturer, Product;
    public uint Connector;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
}

// Read-only ABI: https://raw.githubusercontent.com/microsoft/win32metadata/5c5efbc01d4c87f6830ec304d42777991d533154/generation/WinSDK/RecompiledIdlHeaders/um/wingdi.h
// GET_ADVANCED_COLOR_INFO is 9 (NOT 8); GET_ADVANCED_COLOR_INFO_2 is 15.
[StructLayout(LayoutKind.Sequential)]
internal struct CcdAdvancedColor
{
    public CcdHeader Header;
    public uint Flags, ColorEncoding, BitsPerColorChannel;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CcdAdvancedColor2
{
    public CcdHeader Header;
    public uint Flags, ColorEncoding, BitsPerColorChannel, ActiveColorMode;
}

internal interface ICcdNative
{
    int Sizes(ref uint paths, ref uint modes);
    int Query(ref uint paths, CcdPath[] pathBuffer, ref uint modes, CcdMode[] modeBuffer);
    int SourceName(ref CcdSourceName name);
    int TargetName(ref CcdTargetName name);
    int AdvancedColor(ref CcdAdvancedColor info);
    int AdvancedColor2(ref CcdAdvancedColor2 info);
}

internal sealed class Win32CcdNative : ICcdNative
{
    private const uint ActivePaths = 2;
    public int Sizes(ref uint paths, ref uint modes) => GetDisplayConfigBufferSizes(ActivePaths, ref paths, ref modes);
    public int Query(ref uint paths, CcdPath[] pathBuffer, ref uint modes, CcdMode[] modeBuffer) =>
        QueryDisplayConfig(ActivePaths, ref paths, pathBuffer, ref modes, modeBuffer, IntPtr.Zero);
    public int SourceName(ref CcdSourceName name) => DisplayConfigGetDeviceInfo(ref name);
    public int TargetName(ref CcdTargetName name) => DisplayConfigGetDeviceInfo(ref name);
    public int AdvancedColor(ref CcdAdvancedColor info) => DisplayConfigGetDeviceInfo(ref info);
    public int AdvancedColor2(ref CcdAdvancedColor2 info) => DisplayConfigGetDeviceInfo(ref info);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetDisplayConfigBufferSizes(uint flags, ref uint paths, ref uint modes);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int QueryDisplayConfig(uint flags, ref uint paths, [In, Out] CcdPath[] pathBuffer,
        ref uint modes, [In, Out] CcdMode[] modeBuffer, IntPtr topology);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int DisplayConfigGetDeviceInfo(ref CcdSourceName name);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int DisplayConfigGetDeviceInfo(ref CcdTargetName name);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int DisplayConfigGetDeviceInfo(ref CcdAdvancedColor info);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int DisplayConfigGetDeviceInfo(ref CcdAdvancedColor2 info);
}
