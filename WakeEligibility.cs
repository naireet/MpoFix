using MpoFix.Interop;

namespace MpoFix;

// StableIdentity must come from the owning system's physical inventory, not GDI/friendly/path names.
internal sealed record PhysicalLayout(string? StableIdentity, bool? Primary, uint? Width, uint? Height,
    CcdRational? Refresh, int? X, int? Y, bool? HdrUserEnabled, uint? Rotation, uint? Scaling);

internal enum LayoutMatch { Unknown, MissingMonitor, Exact, Mismatch }
internal enum WakeDecisionKind { Skip, HealthyNoOp, ObservationOnly, OwnerIntegrationRequired }
internal sealed record WakeDecision(WakeDecisionKind Kind, string Reason);
internal enum HostDisplayOwnership { Unknown, Local, ActiveApp, PausedApp, VirtualDisplay, Transition }

internal sealed record WakeCommandOptions(bool RestoreTopology, int DelaySeconds)
{
    internal static bool TryParse(IReadOnlyList<string> arguments, out WakeCommandOptions options)
    {
        options = new(false, 30);
        bool delaySeen = false, restoreSeen = false;
        foreach (var argument in arguments)
        {
            if (argument == "--restore-topology" && !restoreSeen)
            {
                restoreSeen = true;
                options = options with { RestoreTopology = true };
            }
            else if (argument.StartsWith("--delay=", StringComparison.Ordinal) && !delaySeen
                && int.TryParse(argument.AsSpan(8), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int delay) && delay is >= 0 and <= 60)
            {
                delaySeen = true;
                options = options with { DelaySeconds = delay };
            }
            else return false;
        }
        return true;
    }
}

/// <summary>Pure evidence classification only. No snapshot store, host API, executor, lease or restore primitive.</summary>
internal static class WakeEligibility
{
    internal static LayoutMatch Compare(IReadOnlyList<PhysicalLayout>? saved, IReadOnlyList<PhysicalLayout>? observed)
    {
        if (saved is null || saved.Count == 0 || observed is null
            || saved.Concat(observed).Any(d => d is null || string.IsNullOrWhiteSpace(d.StableIdentity))
            || saved.Select(d => d.StableIdentity).Distinct(StringComparer.Ordinal).Count() != saved.Count
            || observed.Select(d => d.StableIdentity).Distinct(StringComparer.Ordinal).Count() != observed.Count)
            return LayoutMatch.Unknown;

        bool mismatch = false;
        foreach (var expected in saved)
        {
            var current = observed.FirstOrDefault(d => d.StableIdentity == expected.StableIdentity);
            if (current is null) return LayoutMatch.MissingMonitor;
            if (!Known(expected) || !Known(current)) return LayoutMatch.Unknown;
            mismatch |= expected != current;
        }
        // Additional physical monitors are not, by themselves, proof that a saved layout is invalid.
        return mismatch ? LayoutMatch.Mismatch : LayoutMatch.Exact;
    }

    internal static WakeDecision Decide(LayoutMatch layout, MpoState mpo, bool restoreRequested,
        bool observationReadable, bool latestConfirmedLocalAvailable, HostDisplayOwnership ownership)
    {
        if (!observationReadable)
            return new(WakeDecisionKind.Skip, "Native/input-desktop topology observation unavailable; no repair.");
        if (ownership != HostDisplayOwnership.Local)
            return new(WakeDecisionKind.Skip, $"Host display ownership={ownership}; protect active AND paused apps, VDD and transitions. Unknown is not Local.");
        if (!latestConfirmedLocalAvailable)
            return new(WakeDecisionKind.Skip, "Latest trusted confirmed Local baseline unavailable; do not import streaming topology.");
        if (layout == LayoutMatch.Exact)
            return new(WakeDecisionKind.HealthyNoOp, $"Saved physical layout is unchanged; MPO={mpo} does not require topology restoration.");
        if (layout == LayoutMatch.MissingMonitor)
            return new(WakeDecisionKind.Skip, "Saved physical monitor missing; establish physical inventory before considering restore.");
        if (layout != LayoutMatch.Mismatch)
            return new(WakeDecisionKind.Skip, "Monitor identity/layout match unknown; MPO Unknown does not establish topology loss.");
        if (!restoreRequested)
            return new(WakeDecisionKind.ObservationOnly, "Layout mismatch observed; no repair requested.");
        return new(WakeDecisionKind.OwnerIntegrationRequired, "Confirmed layout mismatch still requires the owning system's existing serialized display executor. A fresh Local query is not write authority; no restore is implemented here.");
    }

    private static bool Known(PhysicalLayout d) => d.Primary.HasValue && d.Width is > 0 && d.Height is > 0
        && d.Refresh is { Numerator: > 0, Denominator: > 0 } && d.X.HasValue && d.Y.HasValue
        && d.HdrUserEnabled.HasValue && d.Rotation is > 0 && d.Scaling is > 0;
}
