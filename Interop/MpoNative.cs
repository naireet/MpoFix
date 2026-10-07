using System.Runtime.InteropServices;

namespace MpoFix.Interop;

internal sealed record GdiDisplay(string Name, bool IsPrimary);
internal sealed record DisplayEnumeration(IReadOnlyList<GdiDisplay> Displays, bool Complete, string? Error);

internal interface IMpoNative
{
    DisplayEnumeration Enumerate();
    uint Open(ref D3DKMT_OPENADAPTERFROMGDIDISPLAYNAME adapter);
    uint Caps(ref D3DKMT_GET_MULTIPLANE_OVERLAY_CAPS caps);
    uint Close(ref D3DKMT_CLOSEADAPTER adapter);
}

internal sealed class Win32MpoNative : IMpoNative
{
    public DisplayEnumeration Enumerate()
    {
        var result = new List<GdiDisplay>();
        for (uint i = 0; i < 256; i++)
        {
            var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            // FALSE is the documented end-of-enumeration signal, not a GetLastError API.
            if (!EnumDisplayDevicesW(null, i, ref device, 0)) return new(result.AsReadOnly(), true, null);
            if ((device.Flags & 1) != 0 && (device.Flags & 8) == 0)
                result.Add(new(device.Name, (device.Flags & 4) != 0));
        }
        return new(result.AsReadOnly(), false, "Display enumeration bound exceeded");
    }
    public uint Open(ref D3DKMT_OPENADAPTERFROMGDIDISPLAYNAME adapter) => D3DKMT.D3DKMTOpenAdapterFromGdiDisplayName(ref adapter);
    public uint Caps(ref D3DKMT_GET_MULTIPLANE_OVERLAY_CAPS caps) => D3DKMT.D3DKMTGetMultiPlaneOverlayCaps(ref caps);
    public uint Close(ref D3DKMT_CLOSEADAPTER adapter) => D3DKMT.D3DKMTCloseAdapter(ref adapter);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(string? device, uint index, ref DisplayDevice data, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }
}
