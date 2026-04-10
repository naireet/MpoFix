using System.Runtime.InteropServices;
using MpoFix.Interop;

namespace MpoFix;

/// <summary>
/// Tri-state result for MPO detection — distinguishes "unknown" from "mismatch".
/// </summary>
internal enum MpoState { OnPrimary, OffPrimary, Unknown }

/// <summary>
/// Queries per-display MPO plane assignment using D3DKMT kernel thunks (same API as Special K).
/// </summary>
internal static class MpoDetector
{
    internal record DisplayMpoInfo(
        string DeviceName,
        string MonitorName,
        uint VidPnSourceId,
        uint MaxRGBPlanes,
        uint MaxYUVPlanes,
        bool HasMpo,
        bool IsPrimary);

    /// <summary>
    /// Queries MPO capabilities for all active displays via EnumDisplayDevices enumeration.
    /// </summary>
    internal static List<DisplayMpoInfo> QueryAllDisplays()
    {
        var results = new List<DisplayMpoInfo>();

        var dd = new DISPLAY_DEVICE();
        dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();

        for (uint i = 0; EnumDisplayDevicesW(null, i, ref dd, 0); i++)
        {
            // Skip inactive/mirroring devices
            if ((dd.StateFlags & 0x1) == 0) // DISPLAY_DEVICE_ATTACHED_TO_DESKTOP
                continue;

            var info = QueryDisplay(dd.DeviceName);
            if (info is not null)
                results.Add(info);
        }

        return results;
    }

    /// <summary>
    /// Queries MPO capabilities for a single display by GDI name.
    /// </summary>
    internal static DisplayMpoInfo? QueryDisplay(string gdiDisplayName)
    {
        var openAdapter = new D3DKMT_OPENADAPTERFROMGDIDISPLAYNAME
        {
            DeviceName = gdiDisplayName
        };

        uint hr = D3DKMT.D3DKMTOpenAdapterFromGdiDisplayName(ref openAdapter);
        if (!D3DKMT.Succeeded(hr))
            return null;

        try
        {
            var caps = new D3DKMT_GET_MULTIPLANE_OVERLAY_CAPS
            {
                hAdapter = openAdapter.hAdapter,
                VidPnSourceId = openAdapter.VidPnSourceId
            };

            hr = D3DKMT.D3DKMTGetMultiPlaneOverlayCaps(ref caps);
            if (!D3DKMT.Succeeded(hr))
                return null;

            var (monitorName, isPrimary) = GetMonitorInfo(gdiDisplayName);

            return new DisplayMpoInfo(
                DeviceName: gdiDisplayName,
                MonitorName: monitorName,
                VidPnSourceId: openAdapter.VidPnSourceId,
                MaxRGBPlanes: caps.MaxRGBPlanes,
                MaxYUVPlanes: caps.MaxYUVPlanes,
                HasMpo: caps.MaxRGBPlanes > 1,
                IsPrimary: isPrimary);
        }
        finally
        {
            var closeAdapter = new D3DKMT_CLOSEADAPTER { hAdapter = openAdapter.hAdapter };
            D3DKMT.D3DKMTCloseAdapter(ref closeAdapter);
        }
    }

    /// <summary>
    /// Returns true if MPO is currently assigned to the primary display.
    /// </summary>
    internal static bool IsMpoOnPrimary() => GetMpoState() == MpoState.OnPrimary;

    /// <summary>
    /// Tri-state MPO detection: OnPrimary, OffPrimary, or Unknown (detection failed/no displays).
    /// </summary>
    internal static MpoState GetMpoState()
    {
        var displays = QueryAllDisplays();
        if (displays.Count == 0) return MpoState.Unknown;

        var primary = displays.FirstOrDefault(d => d.IsPrimary);
        if (primary is null) return MpoState.Unknown;

        return primary.HasMpo ? MpoState.OnPrimary : MpoState.OffPrimary;
    }

    /// <summary>
    /// Prints MPO status for all displays to the console.
    /// </summary>
    internal static void PrintStatus()
    {
        var displays = QueryAllDisplays();

        if (displays.Count == 0)
        {
            Console.Error.WriteLine("No displays found.");
            return;
        }

        Console.WriteLine($"{"Display",-14} {"Monitor",-20} {"RGB",-5} {"YUV",-5} {"Primary",-9} MPO");
        Console.WriteLine(new string('-', 68));

        foreach (var d in displays)
        {
            string mpoStatus = d.HasMpo ? "YES" : "no";
            string primary = d.IsPrimary ? "YES" : "";
            Console.WriteLine($"{d.DeviceName,-14} {d.MonitorName,-20} {d.MaxRGBPlanes,-5} {d.MaxYUVPlanes,-5} {primary,-9} {mpoStatus}");
        }

        var mpoDisplay = displays.FirstOrDefault(d => d.HasMpo);
        var primaryDisplay = displays.FirstOrDefault(d => d.IsPrimary);
        Console.WriteLine();

        if (mpoDisplay is null)
        {
            Console.WriteLine("WARNING: No display has MPO planes assigned.");
        }
        else if (mpoDisplay.IsPrimary)
        {
            Console.WriteLine($"OK: MPO active on primary display ({mpoDisplay.MonitorName})");
        }
        else
        {
            Console.WriteLine($"MISMATCH: MPO on {mpoDisplay.MonitorName}, but primary is {primaryDisplay?.MonitorName ?? "unknown"}");
            Console.WriteLine("Run 'mpofix fix' to reassign.");
        }
    }

    /// <summary>
    /// Gets monitor friendly name and primary status via EnumDisplayDevices.
    /// Matches by DeviceName string instead of index arithmetic.
    /// Falls back to CCD API for actual EDID monitor name.
    /// </summary>
    private static (string monitorName, bool isPrimary) GetMonitorInfo(string gdiDisplayName)
    {
        bool isPrimary = false;
        string monitorName = "Unknown";

        // Enumerate adapters and match by DeviceName
        var ddAdapter = new DISPLAY_DEVICE();
        ddAdapter.cb = Marshal.SizeOf<DISPLAY_DEVICE>();

        for (uint i = 0; EnumDisplayDevicesW(null, i, ref ddAdapter, 0); i++)
        {
            if (ddAdapter.DeviceName.Equals(gdiDisplayName, StringComparison.OrdinalIgnoreCase))
            {
                isPrimary = (ddAdapter.StateFlags & 0x4) != 0; // DISPLAY_DEVICE_PRIMARY_DEVICE
                break;
            }
        }

        // Use CCD API to get actual EDID monitor name
        monitorName = GetEdidMonitorName(gdiDisplayName) ?? "Unknown";

        return (monitorName, isPrimary);
    }

    /// <summary>
    /// Uses Windows CCD (QueryDisplayConfig + DisplayConfigGetDeviceInfo) to get EDID monitor name.
    /// Retries on ERROR_INSUFFICIENT_BUFFER which can occur during topology churn.
    /// </summary>
    private static string? GetEdidMonitorName(string gdiDisplayName)
    {
        const int ERROR_INSUFFICIENT_BUFFER = 122;
        const int maxRetries = 3;

        DISPLAYCONFIG_PATH_INFO[] paths;
        DISPLAYCONFIG_MODE_INFO[] modes;
        uint pathCount, modeCount;

        // Retry loop handles buffer race during display reconfiguration
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            pathCount = 0; modeCount = 0;
            int err = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, ref pathCount, ref modeCount);
            if (err != 0) return null;

            paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            err = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);

            if (err == 0)
            {
                // Success — search for our display
                for (int i = 0; i < pathCount; i++)
                {
                    var sourceName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                    sourceName.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                    sourceName.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                    sourceName.header.adapterId = paths[i].sourceInfo.adapterId;
                    sourceName.header.id = paths[i].sourceInfo.id;

                    if (DisplayConfigGetDeviceInfo(ref sourceName) != 0)
                        continue;

                    if (!sourceName.viewGdiDeviceName.Equals(gdiDisplayName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var targetName = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                    targetName.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                    targetName.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                    targetName.header.adapterId = paths[i].targetInfo.adapterId;
                    targetName.header.id = paths[i].targetInfo.id;

                    if (DisplayConfigGetDeviceInfo(ref targetName) == 0)
                        return targetName.monitorFriendlyDeviceName;
                }

                return null; // Display not found in paths
            }

            if (err != ERROR_INSUFFICIENT_BUFFER)
                return null; // Non-retryable error

            Thread.Sleep(50); // Brief pause before retry
        }

        return null;
    }

    // --- Win32 Display Config (CCD) interop ---

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, ref uint numPaths, ref uint numModes);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPaths,
        [In, Out] DISPLAYCONFIG_PATH_INFO[] paths, ref uint numModes,
        [In, Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME info);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME info);

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_CCD { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID_CCD adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID_CCD adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID_CCD adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID_CCD adapterId;
        // Union of source/target mode — we just need enough space
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] modeData;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(
        string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }
}
