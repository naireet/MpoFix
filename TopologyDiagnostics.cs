using System.Runtime.InteropServices;
using MpoFix.Interop;

namespace MpoFix;

internal enum ObservationStatus { Available, Transient, Unavailable }

internal sealed record ColorEvidence(int? Info2Error, int? LegacyError, uint? Version,
    uint? RawFlags, uint? ColorEncoding, uint? BitsPerColorChannel, uint? ActiveColorMode)
{
    public bool? AdvancedColorSupported => Flag(0);
    public bool? AdvancedColorActive => Version == 2 ? Flag(1) : null;
    public bool? AdvancedColorEnabled => Version == 1 ? Flag(1) : null;
    public bool? LimitedByPolicy => Version == 2 ? Flag(3) : null;
    public bool? AdvancedColorForceDisabled => Version == 1 ? Flag(3) : null;
    public bool? WideColorEnforced => Version == 1 ? Flag(2) : null;
    public bool? HdrSupported => Version == 2 ? Flag(4) : null;
    public bool? HdrUserEnabled => Version == 2 ? Flag(5) : null;
    public bool? HdrActive => Version == 2 && (ActiveColorMode is 0 or 1 or 2) ? ActiveColorMode == 2 : null;
    public bool? WideColorSupported => Version == 2 ? Flag(6) : null;
    public bool? WideColorUserEnabled => Version == 2 ? Flag(7) : null;

    private bool? Flag(int bit) => RawFlags.HasValue ? (RawFlags.Value & (1u << bit)) != 0 : null;
}

internal sealed record TopologyPath(
    LUID SourceAdapter, uint SourceId, LUID TargetAdapter, uint TargetId,
    string? GdiName, string? FriendlyName, string? DevicePath,
    uint NameFlags, ushort? EdidManufacturer, ushort? EdidProduct,
    uint? Connector, uint OutputTechnology, CcdSourceMode? SourceMode,
    CcdRational Refresh, uint Rotation, uint Scaling, bool TargetAvailable,
    uint PathFlags, uint SourceFlags, uint TargetFlags, ColorEvidence? Color = null)
{
    public bool? IsPrimary => SourceMode is { } mode ? mode.X == 0 && mode.Y == 0 : null;
    public bool? HdrUserEnabled => Color?.HdrUserEnabled;
    public bool? HdrActive => Color?.HdrActive;
}

internal sealed record TopologyResult(ObservationStatus Status, int? NativeError,
    IReadOnlyList<TopologyPath> Paths, string Reason);

internal interface ITopologyDiagnostics
{
    TopologyResult Collect();
}

internal sealed class TopologyCollector(ICcdNative native, IDiagnosticSink log, IObservationClock clock) : ITopologyDiagnostics
{
    internal const int MaxAttempts = 3;
    internal const uint MaxPaths = 256, MaxModes = 1024;

    public TopologyResult Collect()
    {
        try
        {
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                uint pathCount = 0, modeCount = 0;
                int error = native.Sizes(ref pathCount, ref modeCount);
                log.Write("ccd.sizes", $"attempt={attempt}, error={error}, paths={pathCount}, modes={modeCount}");
                if (error == 122)
                {
                    if (attempt < MaxAttempts) clock.Wait(TimeSpan.FromMilliseconds(50));
                    continue;
                }
                if (error != 0) return Failure(error, "GetDisplayConfigBufferSizes");
                if (pathCount > MaxPaths || modeCount > MaxModes)
                    return Failure(null, "Allocation guard: CCD counts exceed limits");

                // Non-null buffers even for a zero-count query; iterate only returned counts.
                var paths = new CcdPath[Math.Max(1, pathCount)];
                var modes = new CcdMode[Math.Max(1, modeCount)];
                uint pathCapacity = pathCount, modeCapacity = modeCount;
                error = native.Query(ref pathCount, paths, ref modeCount, modes);
                log.Write("ccd.query", $"attempt={attempt}, error={error}, paths={pathCount}, modes={modeCount}");
                if (error == 122)
                {
                    if (attempt < MaxAttempts) clock.Wait(TimeSpan.FromMilliseconds(50));
                    continue;
                }
                if (error != 0) return Failure(error, "QueryDisplayConfig");
                if (pathCount > pathCapacity || modeCount > modeCapacity)
                    return Failure(null, "Invalid returned CCD counts");

                var result = new List<TopologyPath>();
                bool complete = pathCount > 0;
                for (int i = 0; i < pathCount; i++)
                {
                    var path = paths[i];
                    var source = new CcdSourceName { Header = Header<CcdSourceName>(1, path.Source.Adapter, path.Source.Id) };
                    var target = new CcdTargetName { Header = Header<CcdTargetName>(2, path.Target.Adapter, path.Target.Id) };
                    int sourceError = native.SourceName(ref source), targetError = native.TargetName(ref target);
                    log.Write("ccd.names", $"path={i}, sourceError={sourceError}, targetError={targetError}");
                    complete &= sourceError == 0 && targetError == 0;
                    CcdSourceMode? sourceMode = null;
                    // QDC_VIRTUAL_MODE_AWARE is not requested; never guess packed mode indexes.
                    bool unpackedIndexes = (path.Flags & 8) == 0;
                    if (unpackedIndexes && path.Source.ModeIndex < modeCount)
                    {
                        var mode = modes[path.Source.ModeIndex];
                        if (mode.Type == 1 && mode.Id == path.Source.Id && mode.Adapter.Equals(path.Source.Adapter))
                            sourceMode = mode.Source;
                    }
                    if (sourceMode is null)
                    {
                        complete = false;
                        log.Write("ccd.mode", $"path={i}, invalid source mode index/type/identity: {path.Source.ModeIndex}");
                    }
                    if (!unpackedIndexes || path.Target.ModeIndex >= modeCount || modes[path.Target.ModeIndex].Type != 2
                        || modes[path.Target.ModeIndex].Id != path.Target.Id
                        || !modes[path.Target.ModeIndex].Adapter.Equals(path.Target.Adapter))
                    {
                        complete = false;
                        log.Write("ccd.mode", $"path={i}, invalid target mode index/type/identity: {path.Target.ModeIndex}");
                    }
                    bool edidValid = targetError == 0 && (target.Flags & 4) != 0;
                    var color = CollectColor(path.Target.Adapter, path.Target.Id);
                    var observation = new TopologyPath(path.Source.Adapter, path.Source.Id, path.Target.Adapter, path.Target.Id,
                        sourceError == 0 ? source.GdiName : null, targetError == 0 ? target.FriendlyName : null,
                        targetError == 0 ? target.DevicePath : null, targetError == 0 ? target.Flags : 0,
                        edidValid ? target.Manufacturer : null, edidValid ? target.Product : null,
                        targetError == 0 ? target.Connector : null, path.Target.OutputTechnology, sourceMode,
                        path.Target.Refresh, path.Target.Rotation, path.Target.Scaling, path.Target.Available != 0,
                        path.Flags, path.Source.StatusFlags, path.Target.StatusFlags, color);
                    result.Add(observation);
                    log.Write("ccd.path", System.Text.Json.JsonSerializer.Serialize(observation,
                        new System.Text.Json.JsonSerializerOptions { IncludeFields = true }));
                }
                const string limitation = "Active paths only; disconnected physical inventory and stable EDID serial are not established. Color evidence is optional; unavailable or legacy-only HDR remains unknown.";
                log.Write("ccd.inventory", limitation);
                return new(complete ? ObservationStatus.Available : ObservationStatus.Unavailable, null,
                    result.AsReadOnly(), complete ? limitation : $"Incomplete path evidence. {limitation}");
            }
            return new(ObservationStatus.Transient, 122, [], "CCD buffer churn exhausted bounded retries");
        }
        catch (DiagnosticException) { throw; }
        catch (Exception ex)
        {
            log.Write("ccd.exception", Diagnostics.ExceptionDetail(ex));
            return new(ObservationStatus.Unavailable, null, [], "CCD native observation failed");
        }
    }

    private TopologyResult Failure(int? error, string reason)
    {
        log.Write("ccd.failure", $"{reason}, error={error?.ToString() ?? "none"}");
        return new(ObservationStatus.Unavailable, error, [], reason);
    }

    private ColorEvidence CollectColor(LUID adapter, uint target)
    {
        int? info2Error = null, legacyError = null;
        string stage = "info2";
        try
        {
            var info2 = new CcdAdvancedColor2 { Header = Header<CcdAdvancedColor2>(15, adapter, target) };
            info2Error = native.AdvancedColor2(ref info2);
            log.Write("ccd.color.info2", $"target={target}, error={info2Error}");
            if (info2Error == 0)
                return new(info2Error, null, 2, info2.Flags, info2.ColorEncoding, info2.BitsPerColorChannel, info2.ActiveColorMode);

            // Older systems can reject the newer request with NOT_SUPPORTED/INVALID_PARAMETER.
            // Preserve these errors; legacy enabled/WCG flags never establish HDR.
            if (info2Error is 50 or 87)
            {
                stage = "legacy";
                var legacy = new CcdAdvancedColor { Header = Header<CcdAdvancedColor>(9, adapter, target) };
                legacyError = native.AdvancedColor(ref legacy);
                log.Write("ccd.color.legacy", $"target={target}, error={legacyError}");
                if (legacyError == 0)
                    return new(info2Error, legacyError, 1, legacy.Flags, legacy.ColorEncoding, legacy.BitsPerColorChannel, null);
            }
        }
        catch (DiagnosticException) { throw; }
        catch (Exception ex) { log.Write($"ccd.color.{stage}.exception", Diagnostics.ExceptionDetail(ex)); }
        return new(info2Error, legacyError, null, null, null, null, null);
    }

    private static CcdHeader Header<T>(uint type, LUID adapter, uint id) => new()
    {
        Type = type,
        Size = (uint)Marshal.SizeOf<T>(),
        Adapter = adapter,
        Id = id
    };
}
