using System.Runtime.InteropServices;
using System.Text.Json;
using MpoFix.Interop;
using NvAPIWrapper.Native.Display;
using Xunit;

namespace MpoFix.Tests;

public sealed class NativeDiagnosticsTests
{
    [Fact]
    public void CcdAbiMatchesNativeSizesAndOffsets()
    {
        Assert.Equal(8, Marshal.SizeOf<LUID>());
        Assert.Equal(8, Marshal.SizeOf<CcdRational>());
        Assert.Equal(20, Marshal.SizeOf<CcdSourceMode>());
        Assert.Equal(48, Marshal.SizeOf<CcdSignal>());
        Assert.Equal(64, Marshal.SizeOf<CcdMode>());
        Assert.Equal(16, Marshal.OffsetOf<CcdMode>(nameof(CcdMode.Source)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<CcdMode>(nameof(CcdMode.Target)).ToInt32());
        Assert.Equal(20, Marshal.SizeOf<CcdSource>());
        Assert.Equal(48, Marshal.SizeOf<CcdTarget>());
        Assert.Equal(72, Marshal.SizeOf<CcdPath>());
        Assert.Equal(20, Marshal.OffsetOf<CcdPath>(nameof(CcdPath.Target)).ToInt32());
        Assert.Equal(68, Marshal.OffsetOf<CcdPath>(nameof(CcdPath.Flags)).ToInt32());
        Assert.Equal(20, Marshal.SizeOf<CcdHeader>());
        Assert.Equal(84, Marshal.SizeOf<CcdSourceName>());
        Assert.Equal(420, Marshal.SizeOf<CcdTargetName>());
        Assert.Equal(36, Marshal.OffsetOf<CcdTargetName>(nameof(CcdTargetName.FriendlyName)).ToInt32());
        Assert.Equal(164, Marshal.OffsetOf<CcdTargetName>(nameof(CcdTargetName.DevicePath)).ToInt32());
        Assert.Equal(80, Marshal.SizeOf<D3DKMT_OPENADAPTERFROMGDIDISPLAYNAME>());
        Assert.Equal(64, Marshal.OffsetOf<D3DKMT_OPENADAPTERFROMGDIDISPLAYNAME>(nameof(D3DKMT_OPENADAPTERFROMGDIDISPLAYNAME.hAdapter)).ToInt32());
        Assert.Equal(68, Marshal.OffsetOf<D3DKMT_OPENADAPTERFROMGDIDISPLAYNAME>(nameof(D3DKMT_OPENADAPTERFROMGDIDISPLAYNAME.AdapterLuid)).ToInt32());
        Assert.Equal(76, Marshal.OffsetOf<D3DKMT_OPENADAPTERFROMGDIDISPLAYNAME>(nameof(D3DKMT_OPENADAPTERFROMGDIDISPLAYNAME.VidPnSourceId)).ToInt32());
        Assert.Equal(32, Marshal.SizeOf<D3DKMT_GET_MULTIPLANE_OVERLAY_CAPS>());
        Assert.Equal(4, Marshal.SizeOf<D3DKMT_CLOSEADAPTER>());
    }

    [Theory]
    [InlineData(1u, 0u, 0u, 0)]
    [InlineData(0xC0000022u, 0u, 0u, 0)]
    [InlineData(0u, 0xC0000001u, 0u, 2)]
    [InlineData(0u, 0u, 0xC0000008u, 2)]
    public void MpoPersistsRawNtstatusAndClosesSuccessfulOpens(uint open, uint caps, uint close, int closes)
    {
        using var owned = new OwnedLog();
        var native = new FakeMpoNative { OpenStatus = open, CapsStatus = caps, CloseStatus = close };
        var result = new MpoQuery(native, new FakeTopology(), owned.Sink(new())).Observe();
        Assert.Equal(MpoState.Unknown, result.State);
        Assert.Equal(2, result.Displays.Count);
        Assert.All(result.Displays, display => Assert.False(string.IsNullOrWhiteSpace(display.DetectionReason)));
        Assert.Equal(closes, native.Closes);
        string content = File.ReadAllText(owned.PathName);
        Assert.Contains($"0x{open:X8}", content);
        if (open == 0) { Assert.Contains($"0x{caps:X8}", content); Assert.Contains($"0x{close:X8}", content); }
        Assert.Equal(open == 0, D3DKMT.Succeeded(open));
    }

    [Fact]
    public void IdleAndQueryFailureAreDifferentUnknownReasons()
    {
        var native = new FakeMpoNative();
        var query = new MpoQuery(native, new FakeTopology(), new MemoryLog());
        Assert.Equal(MpoReason.NoActiveHolder, query.Observe().Reason);
        native.OpenStatus = 0xC0000001;
        Assert.Equal(MpoReason.NoSuccessfulQuery, query.Observe().Reason);
        native.OpenStatus = 0; native.SecondaryOpenStatus = 1; native.PrimaryPlanes = 6;
        Assert.Equal(MpoReason.IncompleteEvidence, query.Observe().Reason);
    }

    [Fact]
    public void DiagnosticCaptureReusesSuppliedTopologyWithoutASecondCcdQuery()
    {
        var topology = new FakeTopology();
        var observed = topology.Collect();
        var result = new MpoQuery(new FakeMpoNative(), topology, new MemoryLog()).Observe(observed);
        Assert.Equal(MpoReason.NoActiveHolder, result.Reason);
        Assert.Equal(1, topology.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EnumerationFailuresRemainVisible(bool throws)
    {
        var log = new MemoryLog();
        var native = new FakeMpoNative { ThrowEnumeration = throws, EnumComplete = false };
        Assert.Equal(MpoState.Unknown, new MpoQuery(native, new FakeTopology(), log).Observe().State);
        Assert.Contains(throws ? "fake enum failure" : "fake enum error", log.Text);
    }

    [Fact]
    public void MaxPlanesNotRgbDeterminesUniqueHolder()
    {
        var native = new FakeMpoNative { SecondaryPlanes = 6 };
        var query = new MpoQuery(native, new FakeTopology(), new MemoryLog());
        Assert.Equal(MpoState.OffPrimary, query.Observe().State);
        native.PrimaryPlanes = 7;
        Assert.Equal(MpoState.OnPrimary, query.Observe().State);
        native.SecondaryPlanes = 7;
        Assert.Equal(MpoReason.AmbiguousHolder, query.Observe().Reason);
    }

    [Fact]
    public void CcdBufferRaceRetriesAndKeepsExactGeometryRefreshAndUnknownHdr()
    {
        var native = new FakeCcd();
        native.QueryErrors.Enqueue(122); native.QueryErrors.Enqueue(122);
        var clock = new FakeClock(); var log = new MemoryLog();
        var result = new TopologyCollector(native, log, clock).Collect();
        Assert.Equal(ObservationStatus.Available, result.Status);
        Assert.Equal(3, native.Queries);
        Assert.Equal(TimeSpan.FromMilliseconds(100), clock.Elapsed);
        var path = Assert.Single(result.Paths);
        Assert.Equal(-3440, path.SourceMode!.Value.X);
        Assert.Equal(27, path.SourceMode.Value.Y);
        Assert.Equal(new CcdRational(144000, 1001), path.Refresh);
        Assert.Equal((ushort)123, path.EdidManufacturer);
        Assert.Null(path.HdrUserEnabled);
        Assert.Null(path.HdrActive);
        Assert.Contains("stable EDID serial are not established", result.Reason);
        Assert.Contains("error=122", log.Text);
        Assert.Contains("fake-target-path", log.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CcdChurnIsBoundedForSizesAndQuery(bool sizes)
    {
        var native = new FakeCcd();
        foreach (int error in new[] { 122, 122, 122, 122 })
            (sizes ? native.SizeErrors : native.QueryErrors).Enqueue(error);
        var result = new TopologyCollector(native, new MemoryLog(), new FakeClock()).Collect();
        Assert.Equal(ObservationStatus.Transient, result.Status);
        Assert.Equal(122, result.NativeError);
        Assert.Equal(sizes ? 0 : 3, native.Queries);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(50)]
    [InlineData(87)]
    public void CcdNativeErrorsAreReturnedAndPersisted(int error)
    {
        using var owned = new OwnedLog();
        var native = new FakeCcd(); native.QueryErrors.Enqueue(error);
        var result = new TopologyCollector(native, owned.Sink(new()), new FakeClock()).Collect();
        Assert.Equal(ObservationStatus.Unavailable, result.Status);
        Assert.Equal(error, result.NativeError);
        Assert.Equal(1, native.Queries);
        Assert.Contains($"error={error}", File.ReadAllText(owned.PathName));
    }

    [Theory]
    [InlineData("paths")]
    [InlineData("modes")]
    [InlineData("returned-paths")]
    [InlineData("returned-modes")]
    [InlineData("source-index")]
    [InlineData("source-type")]
    [InlineData("source-id")]
    [InlineData("target-index")]
    [InlineData("target-type")]
    [InlineData("target-id")]
    [InlineData("packed-mode-indexes")]
    [InlineData("source-name")]
    [InlineData("target-name")]
    public void CcdRejectsInvalidCountsModesAndMissingNames(string boundary)
    {
        var native = new FakeCcd();
        switch (boundary)
        {
            case "paths": native.RequestedPaths = TopologyCollector.MaxPaths + 1; break;
            case "modes": native.RequestedModes = TopologyCollector.MaxModes + 1; break;
            case "returned-paths": native.ReturnedPaths = 2; break;
            case "returned-modes": native.ReturnedModes = 3; break;
            case "source-index": native.SourceIndex = uint.MaxValue; break;
            case "source-type": native.SourceType = 2; break;
            case "source-id": native.SourceId = 99; break;
            case "target-index": native.TargetIndex = uint.MaxValue; break;
            case "target-type": native.TargetType = 1; break;
            case "target-id": native.TargetId = 99; break;
            case "packed-mode-indexes": native.PathFlags = 9; break;
            case "source-name": native.SourceError = 5; break;
            case "target-name": native.TargetError = 5; break;
        }
        Assert.Equal(ObservationStatus.Unavailable, new TopologyCollector(native, new MemoryLog(), new FakeClock()).Collect().Status);
        if (boundary is "paths" or "modes") Assert.Equal(0, native.Queries);
    }

    [Fact]
    public void InvalidEdidFlagsDoNotInventIdentity()
    {
        var result = new TopologyCollector(new FakeCcd { NameFlags = 1 }, new MemoryLog(), new FakeClock()).Collect();
        Assert.Null(Assert.Single(result.Paths).EdidManufacturer);
        Assert.Null(result.Paths[0].EdidProduct);
    }

    [Theory]
    [InlineData(5, 0, 0, "Default", (int)DesktopStatus.AccessDenied, 0)]
    [InlineData(0, 5, 0, "Default", (int)DesktopStatus.Unknown, 1)]
    [InlineData(0, 0, 6, "Default", (int)DesktopStatus.Unknown, 1)]
    [InlineData(0, 0, 0, "Winlogon", (int)DesktopStatus.Other, 1)]
    [InlineData(0, 0, 0, "Default", (int)DesktopStatus.Default, 1)]
    public void DesktopProbeIsReadOnlyAndDistinguishesEvidence(int open, int name, int close, string desktop,
        int expected, int closes)
    {
        var native = new FakeDesktop { OpenError = open, NameError = name, CloseError = close, DesktopName = desktop };
        var log = new MemoryLog();
        Assert.Equal((DesktopStatus)expected, new DesktopProbe(native, log).Observe().Status);
        Assert.Equal(closes, native.Closes);
        Assert.Contains("desktop.open", log.Text);
        if (closes == 1) Assert.Contains("desktop.close", log.Text);
    }

    [Fact]
    public void DesktopCloseExceptionsArePersistedNotSilentlyDiscarded()
    {
        var log = new MemoryLog();
        var native = new FakeDesktop { ThrowClose = true };
        Assert.Equal(DesktopStatus.Unknown, new DesktopProbe(native, log).Observe().Status);
        Assert.Contains("desktop.close.exception", log.Text);
        Assert.Contains("HResult=0x80131509", log.Text);
        Assert.Equal(1, native.Closes);
    }

    [Theory]
    [InlineData("initialize")]
    [InlineData("get")]
    [InlineData("set")]
    [InlineData("unload")]
    public void NvapiFailuresPersistStageTypeHresultAndPathCount(string stage)
    {
        using var owned = new OwnedLog();
        var nvidia = new FakeNvidia { FailStage = stage };
        bool ok = new LegacyMpoRepair(nvidia, () => MpoState.OnPrimary, new FakeTopology(), new FakeClock(), owned.Sink(new())).Fix(force: true);
        Assert.False(ok);
        Assert.Equal(1, nvidia.Unloads);
        var text = File.ReadAllText(owned.PathName);
        Assert.Contains($"nvapi.{stage}.exception", text);
        Assert.Contains("System.InvalidOperationException", text);
        Assert.Contains("HResult=0x80131509", text);
        if (stage is "set" or "unload") Assert.Contains("pathCount=2", text);
    }

    [Fact]
    public void LegacyRepairPreservesForceFlagsWaitAndCurrentConfigObservations()
    {
        var native = new FakeNvidia(); var topology = new FakeTopology(); var clock = new FakeClock(); var log = new MemoryLog();
        var repair = new LegacyMpoRepair(native, () => MpoState.OnPrimary, topology, clock, log);
        Assert.True(repair.Fix());
        Assert.Equal(0, native.Initializes);
        Assert.True(repair.Fix(force: true));
        Assert.Equal(1, native.Sets);
        Assert.Equal(DisplayConfigFlags.DriverReloadAllowed | DisplayConfigFlags.ForceModeEnumeration, native.Flags);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), clock.Elapsed);
        Assert.Equal(2, topology.Calls);
        Assert.Contains("nvapi.before", log.Text);
        Assert.Contains("nvapi.after", log.Text);
        Assert.Contains("state=OnPrimary", log.Text);
    }

    [Fact]
    public void BeforeAfterCountsAndVerificationArePersistedForLegacyRepair()
    {
        using var owned = new OwnedLog();
        var nvidia = new FakeNvidia();
        Assert.True(new LegacyMpoRepair(nvidia, () => MpoState.OnPrimary, new FakeTopology(),
            new FakeClock(), owned.Sink(new())).Fix(force: true));
        var content = File.ReadAllText(owned.PathName);
        Assert.Contains("nvapi.before", content);
        Assert.Contains("nvapi.after", content);
        Assert.Contains("pathCount=2", content);
        Assert.Contains("state=OnPrimary", content);
        Assert.Contains("nvapi.unload", content);
    }

    [Theory]
    [InlineData(true, 2, true)]
    [InlineData(false, 0, false)]
    [InlineData(false, 2, false)]
    public void LegacyDryRunEmptyAndUnknownVerificationHaveNoNewForceFallback(bool dry, int count, bool expected)
    {
        var nvidia = new FakeNvidia { Count = count }; var clock = new FakeClock();
        var result = new LegacyMpoRepair(nvidia, () => MpoState.Unknown, new FakeTopology(), clock, new MemoryLog()).Fix(dry, true);
        Assert.Equal(expected, result);
        Assert.Equal(!dry && count > 0 ? 1 : 0, nvidia.Sets);
    }

    [Fact]
    public void LoggerAppendsCorrelatedTimestampedRecordsAndEscapesNewlines()
    {
        using var owned = new OwnedLog(); var clock = new FakeClock(); var sink = owned.Sink(clock);
        sink.Write("first", "fake message\nnot a forged record");
        clock.Wait(TimeSpan.FromSeconds(1));
        sink.Write("second", "persisted");
        var lines = File.ReadAllLines(owned.PathName);
        Assert.Equal(2, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal("hermetic-run", first.RootElement.GetProperty("runId").GetString());
        Assert.Equal("fake message\nnot a forged record", first.RootElement.GetProperty("message").GetString());
        Assert.True(first.RootElement.GetProperty("timestamp").GetDateTimeOffset() <
            second.RootElement.GetProperty("timestamp").GetDateTimeOffset());
    }

    [Fact]
    public void FileLoggerFailuresAreVisibleAndPreventLegacyWrite()
    {
        using var owned = new OwnedLog();
        var bad = new FileDiagnosticSink(owned.DirectoryPath, new FakeClock(), "fake");
        var exception = Assert.Throws<DiagnosticException>(() => bad.Write("test", "test"));
        Assert.NotNull(exception.InnerException);
        var nvidia = new FakeNvidia();
        Assert.Throws<DiagnosticException>(() =>
            new LegacyMpoRepair(nvidia, () => MpoState.Unknown, new FakeTopology(), new FakeClock(), bad).Fix(force: true));
        Assert.Equal(0, nvidia.Sets);
    }
}
