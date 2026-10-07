using System.Runtime.InteropServices;
using System.Text.Json;
using MpoFix.Interop;
using Xunit;

namespace MpoFix.Tests;

public sealed class ColorDiagnosticsTests
{
    [Fact]
    public void ColorAbiMatchesPinnedSdkSizesAndOffsets()
    {
        Assert.Equal(32, Marshal.SizeOf<CcdAdvancedColor>());
        Assert.Equal(36, Marshal.SizeOf<CcdAdvancedColor2>());
        Assert.Equal(0, Marshal.OffsetOf<CcdAdvancedColor>(nameof(CcdAdvancedColor.Header)).ToInt32());
        Assert.Equal(20, Marshal.OffsetOf<CcdAdvancedColor>(nameof(CcdAdvancedColor.Flags)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<CcdAdvancedColor>(nameof(CcdAdvancedColor.ColorEncoding)).ToInt32());
        Assert.Equal(28, Marshal.OffsetOf<CcdAdvancedColor>(nameof(CcdAdvancedColor.BitsPerColorChannel)).ToInt32());
        Assert.Equal(0, Marshal.OffsetOf<CcdAdvancedColor2>(nameof(CcdAdvancedColor2.Header)).ToInt32());
        Assert.Equal(20, Marshal.OffsetOf<CcdAdvancedColor2>(nameof(CcdAdvancedColor2.Flags)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<CcdAdvancedColor2>(nameof(CcdAdvancedColor2.ColorEncoding)).ToInt32());
        Assert.Equal(28, Marshal.OffsetOf<CcdAdvancedColor2>(nameof(CcdAdvancedColor2.BitsPerColorChannel)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<CcdAdvancedColor2>(nameof(CcdAdvancedColor2.ActiveColorMode)).ToInt32());
    }

    [Fact]
    public void Info2ReportsVerifiedFlagsAndExactTargetHeaderWithoutLegacyFallback()
    {
        var native = new FakeCcd { Info2Error = 0, Info2Flags = 0xFB, ActiveColorMode = 2, ColorEncoding = 2, BitsPerColorChannel = 12 };
        var result = new TopologyCollector(native, new MemoryLog(), new FakeClock()).Collect();
        var color = Assert.Single(result.Paths).Color!;
        Assert.Equal(ObservationStatus.Available, result.Status);
        Assert.Equal(1, native.Info2Calls);
        Assert.Equal(0, native.LegacyColorCalls);
        Assert.Equal(15u, native.Info2Header!.Value.Type);
        Assert.Equal(36u, native.Info2Header.Value.Size);
        Assert.Equal(9u, native.Info2Header.Value.Id);
        Assert.Equal(33u, native.Info2Header.Value.Adapter.LowPart);
        Assert.Equal(0, native.Info2Header.Value.Adapter.HighPart);
        Assert.Equal(2u, color.Version);
        Assert.Equal(0xFBu, color.RawFlags);
        Assert.Equal(2u, color.ColorEncoding);
        Assert.Equal(12u, color.BitsPerColorChannel);
        Assert.Equal(2u, color.ActiveColorMode);
        Assert.True(color.AdvancedColorSupported);
        Assert.True(color.AdvancedColorActive);
        Assert.True(color.LimitedByPolicy);
        Assert.True(color.HdrSupported);
        Assert.True(color.HdrUserEnabled);
        Assert.True(color.HdrActive);
        Assert.True(color.WideColorSupported);
        Assert.True(color.WideColorUserEnabled);
        Assert.Null(color.AdvancedColorEnabled);
        Assert.Null(color.AdvancedColorForceDisabled);
        Assert.Null(color.WideColorEnforced);
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(0u, true)]
    [InlineData(1u, false)]
    [InlineData(1u, true)]
    [InlineData(2u, false)]
    [InlineData(2u, true)]
    public void ActiveHdrAndUserEnabledPreferenceAreSeparate(uint mode, bool userEnabled)
    {
        var native = new FakeCcd { Info2Error = 0, Info2Flags = userEnabled ? 32u : 0u, ActiveColorMode = mode };
        var path = Assert.Single(new TopologyCollector(native, new MemoryLog(), new FakeClock()).Collect().Paths);
        Assert.Equal(userEnabled, path.HdrUserEnabled);
        Assert.Equal(mode == 2, path.HdrActive);
        Assert.Equal(mode, path.Color!.ActiveColorMode);
    }

    [Fact]
    public void UnknownActiveColorEnumIsRetainedWithoutInventingHdrActive()
    {
        var native = new FakeCcd { Info2Error = 0, Info2Flags = 32, ActiveColorMode = 99 };
        var path = Assert.Single(new TopologyCollector(native, new MemoryLog(), new FakeClock()).Collect().Paths);
        Assert.True(path.HdrUserEnabled);
        Assert.Null(path.HdrActive);
        Assert.Equal(99u, path.Color!.ActiveColorMode);
        Assert.Equal(0, native.LegacyColorCalls);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(87)]
    public void LegacyAdvancedColorAndWcgDoNotEstablishHdr(int info2Error)
    {
        var native = new FakeCcd { Info2Error = info2Error, LegacyColorError = 0, LegacyColorFlags = 15, ColorEncoding = 1 };
        var log = new MemoryLog();
        var path = Assert.Single(new TopologyCollector(native, log, new FakeClock()).Collect().Paths);
        var color = path.Color!;
        Assert.Equal(info2Error, color.Info2Error);
        Assert.Equal(0, color.LegacyError);
        Assert.Equal(1u, color.Version);
        Assert.Equal(1, native.LegacyColorCalls);
        Assert.Equal(9u, native.LegacyColorHeader!.Value.Type);
        Assert.Equal(32u, native.LegacyColorHeader.Value.Size);
        Assert.Equal(9u, native.LegacyColorHeader.Value.Id);
        Assert.Equal(33u, native.LegacyColorHeader.Value.Adapter.LowPart);
        Assert.True(color.AdvancedColorSupported);
        Assert.True(color.AdvancedColorEnabled);
        Assert.True(color.WideColorEnforced);
        Assert.True(color.AdvancedColorForceDisabled);
        Assert.Null(color.AdvancedColorActive);
        Assert.Null(color.LimitedByPolicy);
        Assert.Null(color.HdrSupported);
        Assert.Null(path.HdrUserEnabled);
        Assert.Null(path.HdrActive);
        Assert.Null(color.ActiveColorMode);
        Assert.Null(color.WideColorSupported);
        Assert.Null(color.WideColorUserEnabled);
        Assert.Equal(1u, color.ColorEncoding);
        Assert.Equal(10u, color.BitsPerColorChannel);
        Assert.Contains($"error={info2Error}", log.Text);
        Assert.Contains("ccd.color.legacy", log.Text);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(31)]
    [InlineData(1168)]
    public void NonUnsupportedInfo2ErrorsArePreservedWithoutFallbackOrGuesses(int error)
    {
        var native = new FakeCcd { Info2Error = error, LegacyColorError = 0, LegacyColorFlags = 15 };
        var result = new TopologyCollector(native, new MemoryLog(), new FakeClock()).Collect();
        var color = Assert.Single(result.Paths).Color!;
        Assert.Equal(ObservationStatus.Available, result.Status);
        Assert.Equal(error, color.Info2Error);
        Assert.Null(color.LegacyError);
        Assert.Null(color.Version);
        Assert.Null(color.RawFlags);
        Assert.Null(color.ColorEncoding);
        Assert.Null(color.BitsPerColorChannel);
        Assert.Null(color.HdrUserEnabled);
        Assert.Null(color.HdrActive);
        Assert.Equal(0, native.LegacyColorCalls);
    }

    [Fact]
    public void BothQueriesFailKeepsHdrUnknownAndPersistsBothNativeErrors()
    {
        using var owned = new OwnedLog();
        var native = new FakeCcd { Info2Error = 50, LegacyColorError = 5 };
        var result = new TopologyCollector(native, owned.Sink(new FakeClock()), new FakeClock()).Collect();
        var color = Assert.Single(result.Paths).Color!;
        Assert.Equal(50, color.Info2Error);
        Assert.Equal(5, color.LegacyError);
        Assert.Null(color.Version);
        Assert.Null(color.HdrActive);
        Assert.Null(color.HdrUserEnabled);
        var content = File.ReadAllText(owned.PathName);
        Assert.Contains("ccd.color.info2", content);
        Assert.Contains("error=50", content);
        Assert.Contains("ccd.color.legacy", content);
        Assert.Contains("error=5", content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ColorExceptionsAreVisibleOptionalUnknownEvidence(bool legacy)
    {
        var native = new FakeCcd { ThrowInfo2 = !legacy, ThrowLegacyColor = legacy };
        var log = new MemoryLog();
        var result = new TopologyCollector(native, log, new FakeClock()).Collect();
        var color = Assert.Single(result.Paths).Color!;
        Assert.Equal(ObservationStatus.Available, result.Status);
        Assert.Null(color.HdrUserEnabled);
        Assert.Null(color.HdrActive);
        Assert.Contains(legacy ? "ccd.color.legacy.exception" : "ccd.color.info2.exception", log.Text);
        Assert.Contains("HResult=0x80131509", log.Text);
    }

    [Theory]
    [InlineData("ccd.color.info2")]
    [InlineData("ccd.color.legacy")]
    public void ColorDiagnosticPersistenceFailureIsNotSilentlySwallowed(string category)
    {
        var log = new MemoryLog
        {
            OnWrite = c =>
            {
                if (c == category) throw new DiagnosticException("fake log failure", new IOException("fake"));
            }
        };
        Assert.Throws<DiagnosticException>(() => new TopologyCollector(new FakeCcd(), log, new FakeClock()).Collect());
    }

    [Fact]
    public void PersistedPathContainsSeparateHdrPreferenceAndActiveMode()
    {
        using var owned = new OwnedLog();
        var native = new FakeCcd { Info2Error = 0, Info2Flags = 32, ActiveColorMode = 1, BitsPerColorChannel = 12 };
        new TopologyCollector(native, owned.Sink(new FakeClock()), new FakeClock()).Collect();
        string? message = null;
        foreach (var line in File.ReadAllLines(owned.PathName))
        {
            using var entry = JsonDocument.Parse(line);
            if (entry.RootElement.GetProperty("category").GetString() == "ccd.path")
                message = entry.RootElement.GetProperty("message").GetString();
        }
        Assert.NotNull(message);
        using var path = JsonDocument.Parse(message);
        Assert.True(path.RootElement.GetProperty("HdrUserEnabled").GetBoolean());
        Assert.False(path.RootElement.GetProperty("HdrActive").GetBoolean());
        var color = path.RootElement.GetProperty("Color");
        Assert.Equal(1u, color.GetProperty("ActiveColorMode").GetUInt32());
        Assert.Equal(12u, color.GetProperty("BitsPerColorChannel").GetUInt32());
        Assert.Equal(0, color.GetProperty("Info2Error").GetInt32());
    }
}
