using NvAPIWrapper.Native;
using NvAPIWrapper.Native.Display;
using MpoFix.Interop;

namespace MpoFix;

internal interface INvidiaConfiguration
{
    int PathCount { get; }
    void Reapply(DisplayConfigFlags flags);
}

internal interface INvidiaDisplayApi
{
    void Initialize();
    INvidiaConfiguration GetCurrentConfig();
    void Unload();
}

internal sealed class NvidiaDisplayApi : INvidiaDisplayApi
{
    public void Initialize() => GeneralApi.Initialize();
    public INvidiaConfiguration GetCurrentConfig()
    {
        var paths = DisplayApi.GetDisplayConfig();
        return new Configuration(paths.Length, flags => DisplayApi.SetDisplayConfig(paths, flags));
    }
    public void Unload() => GeneralApi.Unload();
    private sealed class Configuration(int count, Action<DisplayConfigFlags> apply) : INvidiaConfiguration
    {
        public int PathCount => count;
        public void Reapply(DisplayConfigFlags flags) => apply(flags);
    }
}

internal sealed class LegacyMpoRepair(INvidiaDisplayApi nvidia, Func<MpoState> detect,
    ITopologyDiagnostics topology, IObservationClock clock, IDiagnosticSink log)
{
    internal bool Fix(bool dryRun = false, bool force = false)
    {
        if (!force && detect() == MpoState.OnPrimary)
        {
            log.Write("nvapi.skip", "MPO already on primary; no repair needed.");
            return true;
        }
        bool success = false;
        string stage = "initialize";
        try
        {
            log.Write("nvapi.initialize", "Starting legacy current-config repair");
            nvidia.Initialize();
            stage = "get";
            var config = nvidia.GetCurrentConfig();
            log.Write("nvapi.get", $"pathCount={config.PathCount}");
            if (config.PathCount == 0)
                log.Write("nvapi.error", "No display paths returned");
            else if (dryRun)
            {
                log.Write("nvapi.dry-run", "Would reapply current config with legacy FORCE flags");
                success = true;
            }
            else
            {
                stage = "before-topology";
                var before = topology.Collect();
                log.Write("nvapi.before", $"topology={before.Status}, pathCount={before.Paths.Count}, error={before.NativeError}");
                stage = "set";
                var flags = DisplayConfigFlags.DriverReloadAllowed | DisplayConfigFlags.ForceModeEnumeration;
                log.Write("nvapi.set", $"pathCount={config.PathCount}, flags={flags}");
                config.Reapply(flags);
                stage = "verification";
                clock.Wait(TimeSpan.FromMilliseconds(1500));
                var after = topology.Collect();
                log.Write("nvapi.after", $"topology={after.Status}, pathCount={after.Paths.Count}, error={after.NativeError}");
                var state = detect();
                log.Write("nvapi.verification", $"state={state}");
                success = state == MpoState.OnPrimary;
            }
        }
        catch (DiagnosticException) { throw; }
        catch (Exception ex)
        {
            log.Write($"nvapi.{stage}.exception", Diagnostics.ExceptionDetail(ex));
        }
        finally
        {
            try
            {
                nvidia.Unload();
                log.Write("nvapi.unload", "Completed");
            }
            catch (DiagnosticException) { throw; }
            catch (Exception ex)
            {
                log.Write("nvapi.unload.exception", Diagnostics.ExceptionDetail(ex));
                success = false;
            }
        }
        return success;
    }
}

internal static class MpoFixer
{
    internal static bool Fix(bool dryRun = false, bool force = false) =>
        new LegacyMpoRepair(new NvidiaDisplayApi(), MpoDetector.GetMpoState,
            new TopologyCollector(new Win32CcdNative(), Diagnostics.Sink, new SystemObservationClock()),
            new SystemObservationClock(), Diagnostics.Sink).Fix(dryRun, force);
}
