using MpoFix.Interop;

namespace MpoFix;

internal sealed class DiagnosticCommands(Func<TopologyResult> topology, Func<DesktopObservation> desktop,
    Func<MpoObservation> mpo, IDiagnosticSink log, IObservationClock clock)
{
    internal const string IntegrationBlocker = "DIAGNOSTICS IMPLEMENTED; ACTUAL RESTORE INTEGRATION BLOCKED. MpoFix has no ownership authorization through Apollo/Vibepollo's existing display executor (active AND paused apps, VDD/transitions), trusted latest confirmed Local baseline path, or stable EDID+serial inventory. No display write or legacy force fallback.";

    internal int Diagnose()
    {
        var input = desktop();
        var layout = topology();
        var assignment = mpo();
        log.Write("diagnose", $"desktop={input.Status}, topology={layout.Status}, MPO={assignment.State}/{assignment.Reason}");
        MpoDetector.PrintStatus(assignment);
        return input.Status == DesktopStatus.Default && layout.Status == ObservationStatus.Available
            && assignment.Reason is MpoReason.ConfirmedHolder or MpoReason.NoActiveHolder ? 0 : 3;
    }

    internal int WakeRecover(bool restoreTopology, int delaySeconds = 30)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(delaySeconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(delaySeconds, 60);
        log.Write("wake.start", $"delay={delaySeconds}s, restoreRequested={restoreTopology}; one observation, no native writes");
        if (delaySeconds > 0)
        {
            log.Write("wake.wait", "No cached eligibility: input desktop and display evidence will be queried after the settle wait.");
            clock.Wait(TimeSpan.FromSeconds(delaySeconds));
        }

        var input = desktop();
        if (input.Status != DesktopStatus.Default)
        {
            log.Write("wake.result", $"outcome=skipped, reason=input-desktop-{input.Status}, error={input.NativeError}, local=unknown, mutation=false");
            return 3;
        }
        var layout = topology();
        if (layout.Status != ObservationStatus.Available)
        {
            log.Write("wake.result", $"outcome=skipped, reason=topology-{layout.Status}, error={layout.NativeError}, detail={layout.Reason}, local=unknown, mutation=false");
            return 3;
        }
        var assignment = mpo();
        MpoDetector.PrintStatus(assignment);
        log.Write("wake.eligibility", $"local=unknown, retainedApp=unknown, displayOwnership=unknown, trustedLocalBaseline=unavailable, physicalLayoutMatch=unknown, MPO={assignment.State}/{assignment.Reason}");
        log.Write("wake.result", $"outcome=skipped, reason=owner-integration-unavailable, mutation=false. {IntegrationBlocker}");
        return 3;
    }

    internal static DiagnosticCommands Create()
    {
        var clock = new SystemObservationClock();
        var collector = new TopologyCollector(new Win32CcdNative(), Diagnostics.Sink, clock);
        var query = new MpoQuery(new Win32MpoNative(), collector, Diagnostics.Sink);
        TopologyResult? currentObservation = null;
        return new(
            () => currentObservation = collector.Collect(),
            () => new DesktopProbe(new Win32DesktopNative(), Diagnostics.Sink).Observe(),
            () => query.Observe(currentObservation), Diagnostics.Sink, clock);
    }
}
