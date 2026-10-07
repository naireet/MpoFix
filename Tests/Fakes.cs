using System.Text;
using MpoFix.Interop;
using NvAPIWrapper.Native.Display;

namespace MpoFix.Tests;

internal sealed class FakeClock : IObservationClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 2, 20, 0, 0, TimeSpan.Zero);
    public TimeSpan Elapsed { get; set; }
    internal Action<TimeSpan>? OnWait;
    public void Wait(TimeSpan duration) { Elapsed += duration; UtcNow += duration; OnWait?.Invoke(duration); }
}

internal sealed class MemoryLog : IDiagnosticSink
{
    internal readonly List<(string Category, string Message)> Entries = [];
    internal Action<string>? OnWrite;
    public void Write(string category, string message)
    {
        OnWrite?.Invoke(category);
        Entries.Add((category, message));
    }
    internal string Text => string.Join("\n", Entries.Select(e => $"{e.Category}: {e.Message}"));
}

internal sealed class OwnedLog : IDisposable
{
    internal string DirectoryPath { get; } = Path.Combine(AppContext.BaseDirectory, "test-logs", Guid.NewGuid().ToString("N"));
    internal string PathName => Path.Combine(DirectoryPath, "mpofix.log");
    internal OwnedLog() => Directory.CreateDirectory(DirectoryPath);
    internal IDiagnosticSink Sink(FakeClock clock) => new FileDiagnosticSink(PathName, clock, "hermetic-run");
    public void Dispose() => Directory.Delete(DirectoryPath, true);
}

internal sealed class FakeTopology : ITopologyDiagnostics
{
    internal int Calls;
    internal TopologyResult Result = new(ObservationStatus.Available, null, [], "Fake only");
    public TopologyResult Collect() { Calls++; return Result; }
}

internal sealed class FakeMpoNative : IMpoNative
{
    internal uint OpenStatus, CapsStatus, CloseStatus;
    internal bool EnumComplete = true;
    internal bool ThrowEnumeration;
    internal int Closes;
    internal uint PrimaryPlanes = 1, SecondaryPlanes = 1;
    internal uint? SecondaryOpenStatus;
    public DisplayEnumeration Enumerate()
    {
        if (ThrowEnumeration) throw new InvalidOperationException("fake enum failure");
        return new([new("primary", true), new("secondary", false)], EnumComplete, EnumComplete ? null : "fake enum error");
    }
    public uint Open(ref D3DKMT_OPENADAPTERFROMGDIDISPLAYNAME adapter)
    {
        adapter.hAdapter = adapter.DeviceName == "primary" ? 1u : 2u;
        adapter.VidPnSourceId = adapter.hAdapter;
        return adapter.hAdapter == 2 && SecondaryOpenStatus.HasValue ? SecondaryOpenStatus.Value : OpenStatus;
    }
    public uint Caps(ref D3DKMT_GET_MULTIPLANE_OVERLAY_CAPS caps)
    {
        caps.MaxPlanes = caps.hAdapter == 1 ? PrimaryPlanes : SecondaryPlanes;
        caps.MaxRGBPlanes = 5;
        caps.MaxYUVPlanes = 5;
        return CapsStatus;
    }
    public uint Close(ref D3DKMT_CLOSEADAPTER adapter) { Closes++; return CloseStatus; }
}

internal sealed class FakeCcd : ICcdNative
{
    internal readonly Queue<int> SizeErrors = new(), QueryErrors = new();
    internal uint RequestedPaths = 1, RequestedModes = 2;
    internal uint? ReturnedPaths, ReturnedModes;
    internal uint SourceIndex, SourceType = 1, TargetIndex = 1, TargetType = 2;
    internal uint SourceId = 7, TargetId = 9;
    internal uint PathFlags = 1;
    internal int Info2Error = 50, LegacyColorError = 50, Info2Calls, LegacyColorCalls;
    internal uint Info2Flags, LegacyColorFlags, ActiveColorMode, ColorEncoding, BitsPerColorChannel = 10;
    internal bool ThrowInfo2, ThrowLegacyColor;
    internal CcdHeader? Info2Header, LegacyColorHeader;
    internal int SourceError, TargetError, Queries;
    internal uint NameFlags = 5;
    internal CcdSourceMode SourceMode = new() { Width = 3440, Height = 1440, X = -3440, Y = 27 };
    internal CcdRational Refresh = new(144000, 1001);
    public int Sizes(ref uint paths, ref uint modes)
    {
        paths = RequestedPaths; modes = RequestedModes;
        return SizeErrors.Count > 0 ? SizeErrors.Dequeue() : 0;
    }
    public int Query(ref uint paths, CcdPath[] pathBuffer, ref uint modes, CcdMode[] modeBuffer)
    {
        Queries++;
        var error = QueryErrors.Count > 0 ? QueryErrors.Dequeue() : 0;
        if (error != 0) return error;
        pathBuffer[0] = new()
        {
            Source = new() { Id = 7, ModeIndex = SourceIndex, Adapter = new() { LowPart = 33 } },
            Target = new()
            {
                Id = 9,
                ModeIndex = TargetIndex,
                Adapter = new() { LowPart = 33 },
                Refresh = Refresh,
                Rotation = 1,
                Scaling = 3,
                Available = 1,
                OutputTechnology = 10,
                StatusFlags = 2
            },
            Flags = PathFlags
        };
        modeBuffer[0] = new() { Type = SourceType, Id = SourceId, Adapter = new() { LowPart = 33 }, Source = SourceMode };
        if (modeBuffer.Length > 1)
            modeBuffer[1] = new() { Type = TargetType, Id = TargetId, Adapter = new() { LowPart = 33 } };
        paths = ReturnedPaths ?? paths;
        modes = ReturnedModes ?? modes;
        return 0;
    }
    public int SourceName(ref CcdSourceName name) { name.GdiName = "fake-gdi"; return SourceError; }
    public int TargetName(ref CcdTargetName name)
    {
        name.FriendlyName = "Fake monitor"; name.DevicePath = "fake-target-path"; name.Flags = NameFlags;
        name.Manufacturer = 123; name.Product = 456; name.Connector = 1;
        return TargetError;
    }
    public int AdvancedColor2(ref CcdAdvancedColor2 info)
    {
        Info2Calls++; Info2Header = info.Header;
        if (ThrowInfo2) throw new InvalidOperationException("fake INFO_2 failure");
        info.Flags = Info2Flags; info.ColorEncoding = ColorEncoding; info.BitsPerColorChannel = BitsPerColorChannel;
        info.ActiveColorMode = ActiveColorMode;
        return Info2Error;
    }
    public int AdvancedColor(ref CcdAdvancedColor info)
    {
        LegacyColorCalls++; LegacyColorHeader = info.Header;
        if (ThrowLegacyColor) throw new InvalidOperationException("fake legacy color failure");
        info.Flags = LegacyColorFlags; info.ColorEncoding = ColorEncoding; info.BitsPerColorChannel = BitsPerColorChannel;
        return LegacyColorError;
    }
}

internal sealed class FakeDesktop : IDesktopNative
{
    internal int OpenError, NameError, CloseError, Closes;
    internal bool ThrowClose;
    internal string DesktopName = "Default";
    public IntPtr Open(out int error) { error = OpenError; return error == 0 ? (IntPtr)123 : IntPtr.Zero; }
    public bool Name(IntPtr handle, StringBuilder buffer, uint bytes, out uint needed, out int error)
    {
        buffer.Append(DesktopName); needed = (uint)((DesktopName.Length + 1) * 2); error = NameError; return error == 0;
    }
    public bool Close(IntPtr handle, out int error)
    {
        Closes++; error = CloseError;
        if (ThrowClose) throw new InvalidOperationException("fake desktop close exception");
        return error == 0;
    }
}

internal sealed class FakeNvidia : INvidiaDisplayApi, INvidiaConfiguration
{
    internal string? FailStage;
    internal int Sets, Unloads, Gets, Initializes;
    internal DisplayConfigFlags? Flags;
    internal int Count = 2;
    public int PathCount => Count;
    private void Check(string stage) { if (FailStage == stage) throw new InvalidOperationException($"fake {stage} failure"); }
    public void Initialize() { Initializes++; Check("initialize"); }
    public INvidiaConfiguration GetCurrentConfig() { Gets++; Check("get"); return this; }
    public void Reapply(DisplayConfigFlags flags) { Sets++; Flags = flags; Check("set"); }
    public void Unload() { Unloads++; Check("unload"); }
}
