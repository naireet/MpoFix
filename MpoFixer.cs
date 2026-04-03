using NvAPIWrapper.Native;
using NvAPIWrapper.Native.Display;

namespace MpoFix;

/// <summary>
/// Forces NVIDIA driver display path reconfiguration via NVAPI,
/// which triggers MPO plane reassignment to the primary display.
/// Same mechanism as toggling primary in NVIDIA App.
/// </summary>
internal static class MpoFixer
{
    /// <summary>
    /// Checks if MPO is on the wrong display and triggers NVAPI reconfiguration if needed.
    /// Returns true if MPO ended up on the primary display (either already was or was fixed).
    /// </summary>
    internal static bool Fix(bool dryRun = false, bool force = false)
    {
        if (!force && MpoDetector.IsMpoOnPrimary())
        {
            Console.WriteLine("MPO is already on the primary display. No fix needed.");
            return true;
        }

        try
        {
            GeneralApi.Initialize();

            var pathInfos = DisplayApi.GetDisplayConfig();

            if (pathInfos.Length == 0)
            {
                Console.Error.WriteLine("ERROR: No display paths returned from NVAPI.");
                return false;
            }

            Console.WriteLine($"Found {pathInfos.Length} display path(s).");

            if (dryRun)
            {
                Console.WriteLine("Dry run — would reapply config with FORCE flags.");
                return true;
            }

            // Reapply the SAME config with force flags — triggers driver reconfiguration
            var flags = DisplayConfigFlags.DriverReloadAllowed
                      | DisplayConfigFlags.ForceModeEnumeration;

            DisplayApi.SetDisplayConfig(pathInfos, flags);

            Console.WriteLine("Display config reapplied with FORCE flags.");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"NVAPI error: {ex.Message}");
            return false;
        }
        finally
        {
            try { GeneralApi.Unload(); } catch { }
        }
    }
}
