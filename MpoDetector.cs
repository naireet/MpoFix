using MpoFix.Interop;

namespace MpoFix;

internal enum MpoState { OnPrimary, OffPrimary, Unknown }
internal enum MpoReason { ConfirmedHolder, NoSuccessfulQuery, NoActiveHolder, IncompleteEvidence, AmbiguousHolder }

internal sealed record DisplayMpoInfo(string DeviceName, string MonitorName, uint VidPnSourceId,
    uint MaxPlanes, uint MaxRGBPlanes, uint MaxYUVPlanes, bool IsPrimary,
    uint? OpenStatus, uint? CapsStatus, uint? CloseStatus, string? Failure)
{
    internal bool HasMpo => CapsStatus == 0 && MaxPlanes > 1;
    internal bool QuerySucceeded => OpenStatus == 0 && CapsStatus == 0;
    internal string DetectionReason => Failure ?? (CloseStatus != 0 ? "Adapter close failed" :
        MaxPlanes > 1 ? "MaxPlanes > 1 holder candidate" : "No MaxPlanes > 1 (possibly idle)");
}

internal sealed record MpoObservation(MpoState State, MpoReason Reason, IReadOnlyList<DisplayMpoInfo> Displays);

internal sealed class MpoQuery(IMpoNative native, ITopologyDiagnostics topology, IDiagnosticSink log)
{
    internal MpoObservation Observe(TopologyResult? topologyObservation = null)
    {
        var results = new List<DisplayMpoInfo>();
        try
        {
            var enumeration = native.Enumerate();
            log.Write("mpo.enumeration", $"complete={enumeration.Complete}, error={enumeration.Error}, count={enumeration.Displays.Count}");
            var names = topologyObservation ?? topology.Collect();
            foreach (var display in enumeration.Displays)
            {
                uint? openStatus = null, capsStatus = null, closeStatus = null;
                string? failure = null;
                var open = new D3DKMT_OPENADAPTERFROMGDIDISPLAYNAME { DeviceName = display.Name };
                var caps = new D3DKMT_GET_MULTIPLANE_OVERLAY_CAPS();
                try
                {
                    openStatus = native.Open(ref open);
                    log.Write("mpo.open", $"{display.Name}: NTSTATUS=0x{openStatus:X8}");
                    if (openStatus == 0)
                    {
                        caps.hAdapter = open.hAdapter;
                        caps.VidPnSourceId = open.VidPnSourceId;
                        capsStatus = native.Caps(ref caps);
                        log.Write("mpo.caps", $"{display.Name}: NTSTATUS=0x{capsStatus:X8}, source={caps.VidPnSourceId}, planes={caps.MaxPlanes}, RGB={caps.MaxRGBPlanes}, YUV={caps.MaxYUVPlanes}");
                    }
                    failure = openStatus != 0 ? "Adapter open failed" : capsStatus != 0 ? "MPO caps query failed" : null;
                }
                catch (DiagnosticException) { throw; }
                catch (Exception ex)
                {
                    failure = Diagnostics.ExceptionDetail(ex);
                    log.Write("mpo.exception", $"{display.Name}: {failure}");
                }
                finally
                {
                    if (openStatus == 0)
                    {
                        try
                        {
                            var close = new D3DKMT_CLOSEADAPTER { hAdapter = open.hAdapter };
                            closeStatus = native.Close(ref close);
                            log.Write("mpo.close", $"{display.Name}: NTSTATUS=0x{closeStatus:X8}");
                        }
                        catch (DiagnosticException) { throw; }
                        catch (Exception ex)
                        {
                            failure = Diagnostics.ExceptionDetail(ex);
                            log.Write("mpo.close.exception", $"{display.Name}: {failure}");
                        }
                    }
                }
                string monitorName = names.Paths.FirstOrDefault(p =>
                    string.Equals(p.GdiName, display.Name, StringComparison.OrdinalIgnoreCase))?.FriendlyName ?? "Unknown";
                var info = new DisplayMpoInfo(display.Name, monitorName, open.VidPnSourceId, caps.MaxPlanes, caps.MaxRGBPlanes,
                    caps.MaxYUVPlanes, display.IsPrimary, openStatus, capsStatus, closeStatus, failure);
                results.Add(info);
                log.Write("mpo.display", $"{display.Name}, monitor={monitorName}, primary={display.IsPrimary}, reason={info.DetectionReason}");
            }
            return Classify(results, enumeration.Complete);
        }
        catch (DiagnosticException) { throw; }
        catch (Exception ex)
        {
            log.Write("mpo.enumeration.exception", Diagnostics.ExceptionDetail(ex));
            return Classify(results, false);
        }
    }

    private MpoObservation Classify(List<DisplayMpoInfo> displays, bool complete)
    {
        MpoReason reason;
        MpoState state = MpoState.Unknown;
        var queried = displays.Where(d => d.QuerySucceeded).ToArray();
        if (queried.Length == 0) reason = MpoReason.NoSuccessfulQuery;
        else if (!complete || displays.Any(d => !d.QuerySucceeded || d.CloseStatus != 0 || d.Failure is not null)
                 || displays.Count(d => d.IsPrimary) != 1) reason = MpoReason.IncompleteEvidence;
        else
        {
            var holder = MpoDetector.GetMpoHolder(displays);
            if (holder is null) reason = MpoReason.NoActiveHolder;
            else if (queried.Count(d => d.MaxPlanes == holder.MaxPlanes) > 1) reason = MpoReason.AmbiguousHolder;
            else
            {
                reason = MpoReason.ConfirmedHolder;
                state = holder.IsPrimary ? MpoState.OnPrimary : MpoState.OffPrimary;
            }
        }
        log.Write("mpo.result", $"state={state}, reason={reason}, queried={queried.Length}/{displays.Count}");
        return new(state, reason, displays.AsReadOnly());
    }
}

internal static class MpoDetector
{
    private static MpoQuery CreateQuery() => new(new Win32MpoNative(),
        new TopologyCollector(new Win32CcdNative(), Diagnostics.Sink, new SystemObservationClock()), Diagnostics.Sink);

    internal static List<DisplayMpoInfo> QueryAllDisplays() => CreateQuery().Observe().Displays.ToList();
    internal static MpoState GetMpoState() => CreateQuery().Observe().State;
    internal static bool IsMpoOnPrimary() => GetMpoState() == MpoState.OnPrimary;

    internal static DisplayMpoInfo? GetMpoHolder(IReadOnlyList<DisplayMpoInfo> displays) =>
        displays.Where(d => d.HasMpo).OrderByDescending(d => d.MaxPlanes).FirstOrDefault();

    internal static void PrintStatus() => PrintStatus(CreateQuery().Observe());

    internal static void PrintStatus(MpoObservation observation)
    {
        Console.WriteLine($"{"Display",-14} {"Monitor",-20} {"Planes",-7} {"RGB",-5} {"YUV",-5} {"Primary",-9} MPO");
        foreach (var d in observation.Displays)
            Console.WriteLine($"{d.DeviceName,-14} {d.MonitorName,-20} {d.MaxPlanes,-7} {d.MaxRGBPlanes,-5} {d.MaxYUVPlanes,-5} {(d.IsPrimary ? "YES" : ""),-9} {(d.QuerySucceeded ? d.HasMpo ? "YES" : "no" : "unknown")} {d.DetectionReason}");
        Console.WriteLine($"MPO: {observation.State} ({observation.Reason})");
        if (observation.Reason == MpoReason.NoActiveHolder)
            Console.WriteLine("No MaxPlanes > 1 holder; this can be idle and does not establish topology loss or displays-not-ready.");
        if (observation.State == MpoState.OffPrimary)
            Console.WriteLine("MISMATCH: MPO off primary. Run 'mpofix fix' to reapply current NVIDIA config.");
    }
}
