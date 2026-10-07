using MpoFix.Interop;
using Xunit;

namespace MpoFix.Tests;

public sealed class WakeEligibilityTests
{
    private static PhysicalLayout Primary() => new("owner-stable-primary", true, 3840, 2160, new(120000, 1001), 0, 0, true, 1, 3);
    private static PhysicalLayout Secondary() => new("owner-stable-secondary", false, 1440, 2560, new(60000, 1001), -1440, 130, false, 2, 1);

    [Theory]
    [InlineData((int)MpoState.Unknown)]
    [InlineData((int)MpoState.OnPrimary)]
    [InlineData((int)MpoState.OffPrimary)]
    public void HealthyConfirmedLocalLayoutIsNoOpRegardlessOfMpo(int state)
    {
        Assert.Equal(LayoutMatch.Exact, WakeEligibility.Compare([Primary(), Secondary()], [Secondary(), Primary()]));
        var decision = WakeEligibility.Decide(LayoutMatch.Exact, (MpoState)state, true, true, true, HostDisplayOwnership.Local);
        Assert.Equal(WakeDecisionKind.HealthyNoOp, decision.Kind);
    }

    [Fact]
    public void UnknownMpoDoesNotEstablishTopologyLoss()
    {
        var decision = WakeEligibility.Decide(LayoutMatch.Unknown, MpoState.Unknown, true, true, true, HostDisplayOwnership.Local);
        Assert.Equal(WakeDecisionKind.Skip, decision.Kind);
        Assert.Contains("does not establish topology loss", decision.Reason);
    }

    [Theory]
    [InlineData((int)HostDisplayOwnership.Unknown)]
    [InlineData((int)HostDisplayOwnership.ActiveApp)]
    [InlineData((int)HostDisplayOwnership.PausedApp)]
    [InlineData((int)HostDisplayOwnership.VirtualDisplay)]
    [InlineData((int)HostDisplayOwnership.Transition)]
    public void NonLocalOrUnknownOwnershipSkipsEvenWithExactPhysicalLayout(int ownership)
    {
        foreach (var layout in new[] { LayoutMatch.Exact, LayoutMatch.Mismatch })
        {
            var decision = WakeEligibility.Decide(layout, MpoState.Unknown, true, true, true, (HostDisplayOwnership)ownership);
            Assert.Equal(WakeDecisionKind.Skip, decision.Kind);
            Assert.Contains("active AND paused", decision.Reason);
            Assert.Contains("Unknown is not Local", decision.Reason);
        }
    }

    [Fact]
    public void MismatchClassificationDoesNotImplementOrClaimARestore()
    {
        Assert.Equal(WakeDecisionKind.ObservationOnly,
            WakeEligibility.Decide(LayoutMatch.Mismatch, MpoState.OffPrimary, false, true, true, HostDisplayOwnership.Local).Kind);
        var proposed = WakeEligibility.Decide(LayoutMatch.Mismatch, MpoState.OffPrimary, true, true, true, HostDisplayOwnership.Local);
        Assert.Equal(WakeDecisionKind.OwnerIntegrationRequired, proposed.Kind);
        Assert.Contains("no restore is implemented", proposed.Reason);
        Assert.Contains("fresh Local query is not write authority", proposed.Reason);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void MissingNativeDesktopOrLatestLocalEvidenceSkips(bool readable, bool latestLocal)
    {
        Assert.Equal(WakeDecisionKind.Skip,
            WakeEligibility.Decide(LayoutMatch.Exact, MpoState.OnPrimary, true, readable, latestLocal, HostDisplayOwnership.Local).Kind);
    }

    [Fact]
    public void UnknownIdentityAndMissingPhysicalMonitorSkip()
    {
        Assert.Equal(LayoutMatch.Unknown, WakeEligibility.Compare([Primary()], [Primary() with { StableIdentity = null }]));
        Assert.Equal(LayoutMatch.Unknown, WakeEligibility.Compare([Primary()], [Primary(), Primary()]));
        Assert.Equal(LayoutMatch.Unknown, WakeEligibility.Compare(null, [Primary()]));
        Assert.Equal(LayoutMatch.MissingMonitor, WakeEligibility.Compare([Primary(), Secondary()], [Primary()]));
        Assert.Equal(WakeDecisionKind.Skip,
            WakeEligibility.Decide(LayoutMatch.MissingMonitor, MpoState.Unknown, true, true, true, HostDisplayOwnership.Local).Kind);
    }

    [Fact]
    public void AdditionalPhysicalMonitorIsNotAnInventedBlanketRejection()
    {
        var additional = Secondary() with { StableIdentity = "owner-stable-new-monitor", X = 3840, Y = 0 };
        Assert.Equal(LayoutMatch.Exact, WakeEligibility.Compare([Primary(), Secondary()], [Primary(), Secondary(), additional]));
    }

    [Theory]
    [InlineData("primary")]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("refresh")]
    [InlineData("position")]
    [InlineData("hdr-preference")]
    [InlineData("rotation")]
    [InlineData("scaling")]
    public void ExactMatchingPreservesEverySavedFieldWithoutRounding(string field)
    {
        var expected = Secondary();
        var changed = field switch
        {
            "primary" => expected with { Primary = true },
            "width" => expected with { Width = 1080 },
            "height" => expected with { Height = 1920 },
            "refresh" => expected with { Refresh = new CcdRational(60, 1) },
            "position" => expected with { Y = 0 },
            "hdr-preference" => expected with { HdrUserEnabled = true },
            "rotation" => expected with { Rotation = 1 },
            "scaling" => expected with { Scaling = 3 },
            _ => throw new InvalidOperationException()
        };
        Assert.Equal(LayoutMatch.Mismatch, WakeEligibility.Compare([expected], [changed]));
        Assert.Equal(LayoutMatch.Unknown, WakeEligibility.Compare([expected], [expected with { HdrUserEnabled = null }]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProductionWakeCommandRemainsOnlyBlockedDiagnosticCapture(bool optIn)
    {
        var log = new MemoryLog();
        var commands = new DiagnosticCommands(
            () => new(ObservationStatus.Available, null, [], "fake"),
            () => new(DesktopStatus.Default, "Default", null),
            () => new(MpoState.Unknown, MpoReason.NoActiveHolder, []), log, new FakeClock());
        Assert.Equal(3, commands.WakeRecover(optIn));
        Assert.Contains("DIAGNOSTICS IMPLEMENTED", log.Text);
        Assert.Contains("ACTUAL RESTORE INTEGRATION BLOCKED", log.Text);
        Assert.Contains("active AND paused", log.Text);
        Assert.Contains("latest confirmed Local baseline path", log.Text);
        Assert.Contains("No display write or legacy force fallback", log.Text);
        Assert.Contains("local=unknown", log.Text);
        Assert.Contains("mutation=false", log.Text);
    }

    [Fact]
    public void DiagnoseCapturesIdleAndUnavailableEvidenceWithoutRepair()
    {
        var commands = new DiagnosticCommands(
            () => new(ObservationStatus.Available, null, [], "fake"),
            () => new(DesktopStatus.Default, "Default", null),
            () => new(MpoState.Unknown, MpoReason.NoActiveHolder, []), new MemoryLog(), new FakeClock());
        Assert.Equal(0, commands.Diagnose());
        var unavailable = new DiagnosticCommands(
            () => new(ObservationStatus.Unavailable, 5, [], "fake denied"),
            () => new(DesktopStatus.AccessDenied, null, 5),
            () => new(MpoState.Unknown, MpoReason.NoSuccessfulQuery, []), new MemoryLog(), new FakeClock());
        Assert.Equal(3, unavailable.Diagnose());
    }

    [Fact]
    public void FreshLocalObservationDoesNotGrantWriteAuthorityAfterPriorHealthyDecision()
    {
        var initial = WakeEligibility.Decide(LayoutMatch.Exact, MpoState.Unknown, true, true, true, HostDisplayOwnership.Local);
        Assert.Equal(WakeDecisionKind.HealthyNoOp, initial.Kind);
        var current = WakeEligibility.Decide(LayoutMatch.Mismatch, MpoState.OffPrimary, true, true, true, HostDisplayOwnership.Transition);
        Assert.Equal(WakeDecisionKind.Skip, current.Kind);
        var stillLocal = WakeEligibility.Decide(LayoutMatch.Mismatch, MpoState.OffPrimary, true, true, true, HostDisplayOwnership.Local);
        Assert.Equal(WakeDecisionKind.OwnerIntegrationRequired, stillLocal.Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(60)]
    public void WakeMakesOneFreshCaptureOnlyAfterBoundedSettleWait(int delay)
    {
        var clock = new FakeClock();
        int desktopReads = 0, topologyReads = 0, mpoReads = 0;
        var log = new MemoryLog();
        var commands = new DiagnosticCommands(
            () => { topologyReads++; Assert.Equal(TimeSpan.FromSeconds(delay), clock.Elapsed); return new(ObservationStatus.Available, null, [], "fake"); },
            () => { desktopReads++; Assert.Equal(TimeSpan.FromSeconds(delay), clock.Elapsed); return new(DesktopStatus.Default, "Default", null); },
            () => { mpoReads++; return new(MpoState.OnPrimary, MpoReason.ConfirmedHolder, []); }, log, clock);
        Assert.Equal(3, commands.WakeRecover(true, delay));
        Assert.Equal(1, desktopReads);
        Assert.Equal(1, topologyReads);
        Assert.Equal(1, mpoReads);
        Assert.Contains("owner-integration-unavailable", log.Text);
        Assert.DoesNotContain("outcome=healthy", log.Text);
    }

    [Fact]
    public void LockedDesktopDuringWaitSkipsUsingFreshEvidenceWithoutFurtherQueries()
    {
        var clock = new FakeClock();
        var status = DesktopStatus.Default;
        clock.OnWait = _ => status = DesktopStatus.Other;
        var log = new MemoryLog();
        var commands = new DiagnosticCommands(
            () => throw new InvalidOperationException("topology must not be queried"),
            () => new(status, "Winlogon", null),
            () => throw new InvalidOperationException("MPO must not be queried"), log, clock);
        Assert.Equal(3, commands.WakeRecover(true));
        Assert.Equal(TimeSpan.FromSeconds(30), clock.Elapsed);
        Assert.Contains("input-desktop-Other", log.Text);
    }

    [Theory]
    [InlineData((int)ObservationStatus.Transient, 122)]
    [InlineData((int)ObservationStatus.Unavailable, 5)]
    public void WakeQueryFailureIsAnExplicitSkipNotHealthy(int status, int error)
    {
        using var owned = new OwnedLog();
        var clock = new FakeClock();
        var commands = new DiagnosticCommands(
            () => new((ObservationStatus)status, error, [], "fake failure"),
            () => new(DesktopStatus.Default, "Default", null),
            () => throw new InvalidOperationException("MPO must not be queried"), owned.Sink(clock), clock);
        Assert.Equal(3, commands.WakeRecover(true, 0));
        var text = File.ReadAllText(owned.PathName);
        Assert.Contains($"error={error}", text);
        Assert.Contains("outcome=skipped", text);
        Assert.Contains("mutation=false", text);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(61)]
    public void InvalidWakeDelayCannotQueryNativeBoundaries(int delay)
    {
        var commands = new DiagnosticCommands(
            () => throw new InvalidOperationException(), () => throw new InvalidOperationException(),
            () => throw new InvalidOperationException(), new MemoryLog(), new FakeClock());
        Assert.Throws<ArgumentOutOfRangeException>(() => commands.WakeRecover(true, delay));
    }

    [Fact]
    public void WakeLoggingFailureStopsBeforeWaitingOrObservation()
    {
        using var owned = new OwnedLog();
        var clock = new FakeClock();
        var commands = new DiagnosticCommands(
            () => throw new InvalidOperationException(), () => throw new InvalidOperationException(),
            () => throw new InvalidOperationException(), new FileDiagnosticSink(owned.DirectoryPath, clock, "fake"), clock);
        Assert.Throws<DiagnosticException>(() => commands.WakeRecover(true));
        Assert.Equal(TimeSpan.Zero, clock.Elapsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--force")]
    [InlineData("--delay=-1")]
    [InlineData("--delay=61")]
    [InlineData("--delay=2147483648")]
    [InlineData("--delay=+1")]
    [InlineData("--delay=1.5")]
    [InlineData("--delay= 1")]
    public void WakeRejectsUnknownOrUnboundedOptions(string option)
    {
        Assert.False(WakeCommandOptions.TryParse([option], out _));
    }

    [Fact]
    public void WakeOptionsHaveBoundedDefaultsAndRejectDuplicates()
    {
        Assert.True(WakeCommandOptions.TryParse([], out var defaults));
        Assert.Equal(new WakeCommandOptions(false, 30), defaults);
        Assert.True(WakeCommandOptions.TryParse(["--delay=0", "--restore-topology"], out var explicitOptions));
        Assert.Equal(new WakeCommandOptions(true, 0), explicitOptions);
        Assert.False(WakeCommandOptions.TryParse(["--delay=1", "--delay=2"], out _));
        Assert.False(WakeCommandOptions.TryParse(["--restore-topology", "--restore-topology"], out _));
    }
}
