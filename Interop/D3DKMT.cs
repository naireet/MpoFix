using System.Runtime.InteropServices;

namespace MpoFix.Interop;

/// <summary>
/// P/Invoke declarations for D3DKMT (Direct3D Kernel Mode Thunk) functions from gdi32.dll.
/// Used to query per-display MPO (Multi-Plane Overlay) capabilities.
/// </summary>
internal static class D3DKMT
{
    private const uint STATUS_SUCCESS = 0x00000000;

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint D3DKMTOpenAdapterFromGdiDisplayName(
        ref D3DKMT_OPENADAPTERFROMGDIDISPLAYNAME pData);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    internal static extern uint D3DKMTGetMultiPlaneOverlayCaps(
        ref D3DKMT_GET_MULTIPLANE_OVERLAY_CAPS pData);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    internal static extern uint D3DKMTCloseAdapter(
        ref D3DKMT_CLOSEADAPTER pData);

    internal static bool Succeeded(uint ntstatus) => ntstatus == STATUS_SUCCESS;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct D3DKMT_OPENADAPTERFROMGDIDISPLAYNAME
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string DeviceName;

    public uint hAdapter;
    public LUID AdapterLuid;
    public uint VidPnSourceId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LUID
{
    public uint LowPart;
    public int HighPart;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3DKMT_GET_MULTIPLANE_OVERLAY_CAPS
{
    public uint hAdapter;
    public uint VidPnSourceId;
    public uint MaxPlanes;
    public uint MaxRGBPlanes;
    public uint MaxYUVPlanes;
    public uint OverlayCaps;
    public float MaxStretchFactor;
    public float MaxShrinkFactor;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3DKMT_CLOSEADAPTER
{
    public uint hAdapter;
}
