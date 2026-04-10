using MpoFix;

// Exit codes: 0=ok (already correct), 1=error, 2=fixed successfully, 3=unresolved
const int EXIT_OK = 0;
const int EXIT_ERROR = 1;
const int EXIT_FIXED = 2;
const int EXIT_UNRESOLVED = 3;

// File logging for hidden scheduled task visibility
var logPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "mpofix.log");

void Log(string message)
{
    var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
    Console.WriteLine(line);
    try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { }
}

if (args.Length == 0 || args[0] is "status" or "-s")
{
    MpoDetector.PrintStatus();
    return EXIT_OK;
}

if (args[0] is "fix" or "-f")
{
    bool dryRun = args.Contains("--dry-run");
    bool force = args.Contains("--force");

    Console.WriteLine("=== Before ===");
    MpoDetector.PrintStatus();
    Console.WriteLine();

    bool ok = MpoFixer.Fix(dryRun: dryRun, force: force);
    if (!ok) return EXIT_ERROR;

    if (!dryRun)
    {
        Console.WriteLine();
        Console.WriteLine("=== After ===");
        MpoDetector.PrintStatus();
    }

    return EXIT_FIXED;
}

if (args[0] is "settle-fix" or "settle" or "-sf")
{
    int delay = 15;
    int retries = 3;
    var delayArg = args.FirstOrDefault(a => a.StartsWith("--delay="));
    if (delayArg is not null && int.TryParse(delayArg.Split('=')[1], out int parsedDelay))
        delay = Math.Max(1, parsedDelay);
    var retriesArg = args.FirstOrDefault(a => a.StartsWith("--retries="));
    if (retriesArg is not null && int.TryParse(retriesArg.Split('=')[1], out int parsedRetries))
        retries = Math.Max(1, parsedRetries);

    Log($"settle-fix started (delay={delay}s, retries={retries})");

    // Initial delay for display topology to stabilize after wake
    Log($"Waiting {delay}s for displays to settle...");
    Thread.Sleep(delay * 1000);

    for (int attempt = 1; attempt <= retries; attempt++)
    {
        var state = MpoDetector.GetMpoState();
        Log($"Attempt {attempt}/{retries}: state={state}");

        switch (state)
        {
            case MpoState.OnPrimary:
                Log("MPO already on primary — no fix needed.");
                return EXIT_OK;

            case MpoState.OffPrimary:
                Log("MISMATCH detected — confirming in 5s...");
                Thread.Sleep(5000);

                var confirmed = MpoDetector.GetMpoState();
                if (confirmed == MpoState.OnPrimary)
                {
                    Log("False positive — MPO now correct, skipping fix.");
                    return EXIT_OK;
                }

                Log("MISMATCH confirmed — fixing...");
                bool ok = MpoFixer.Fix();
                if (ok)
                {
                    Log("Fix applied and verified.");
                    MpoDetector.PrintStatus();
                    return EXIT_FIXED;
                }

                Log("Fix applied but verification failed.");
                break;

            case MpoState.Unknown:
                Log("Detection returned Unknown (displays not ready).");
                break;
        }

        if (attempt < retries)
        {
            int backoff = delay * attempt;
            Log($"Backing off {backoff}s before retry...");
            Thread.Sleep(backoff * 1000);
        }
    }

    // Last resort: force fix even if detection is uncertain
    Log("Retries exhausted — attempting force fix...");
    bool forced = MpoFixer.Fix(force: true);
    if (forced)
    {
        Log("Force fix applied and verified.");
        MpoDetector.PrintStatus();
        return EXIT_FIXED;
    }

    Log("UNRESOLVED: Could not fix MPO assignment.");
    MpoDetector.PrintStatus();
    return EXIT_UNRESOLVED;
}

// Legacy watch mode — redirect to settle-fix
if (args[0] is "watch" or "-w")
{
    Console.Error.WriteLine("NOTE: 'watch' is deprecated. Use 'settle-fix' instead.");
    int timeout = 60;
    var timeoutArg = args.FirstOrDefault(a => a.StartsWith("--timeout="));
    if (timeoutArg is not null && int.TryParse(timeoutArg.Split('=')[1], out int parsedTimeout))
        timeout = Math.Max(10, parsedTimeout);

    // Map old watch params to settle-fix equivalents
    args = ["settle-fix", $"--delay={Math.Min(timeout / 4, 20)}", "--retries=3"];
    // Re-run with new args (simple goto-like recursion via process)
    Log($"Redirecting watch --timeout={timeout} to settle-fix");

    // Just inline the settle-fix logic with mapped params
    int delay = Math.Min(timeout / 4, 20);
    int retries = 3;

    Log($"settle-fix started (delay={delay}s, retries={retries})");
    Log($"Waiting {delay}s for displays to settle...");
    Thread.Sleep(delay * 1000);

    for (int attempt = 1; attempt <= retries; attempt++)
    {
        var state = MpoDetector.GetMpoState();
        Log($"Attempt {attempt}/{retries}: state={state}");

        if (state == MpoState.OnPrimary)
        {
            Log("MPO already on primary — no fix needed.");
            return EXIT_OK;
        }

        if (state == MpoState.OffPrimary)
        {
            Log("MISMATCH detected — confirming in 5s...");
            Thread.Sleep(5000);
            if (MpoDetector.GetMpoState() == MpoState.OnPrimary)
            {
                Log("False positive — MPO now correct.");
                return EXIT_OK;
            }
            Log("MISMATCH confirmed — fixing...");
            bool ok = MpoFixer.Fix();
            if (ok) { Log("Fix applied and verified."); MpoDetector.PrintStatus(); return EXIT_FIXED; }
        }

        if (attempt < retries)
        {
            int backoff = delay * attempt;
            Log($"Backing off {backoff}s before retry...");
            Thread.Sleep(backoff * 1000);
        }
    }

    Log("Retries exhausted — attempting force fix...");
    bool forced = MpoFixer.Fix(force: true);
    if (forced) { Log("Force fix applied and verified."); MpoDetector.PrintStatus(); return EXIT_FIXED; }
    Log("UNRESOLVED: Could not fix MPO assignment.");
    return EXIT_UNRESOLVED;
}

Console.Error.WriteLine("Usage: mpofix [status|fix|settle-fix] [options]");
Console.Error.WriteLine();
Console.Error.WriteLine("Commands:");
Console.Error.WriteLine("  status             Show per-display MPO plane assignment");
Console.Error.WriteLine("  fix                Fix MPO if on wrong display (skips if already correct)");
Console.Error.WriteLine("  settle-fix         Wait for displays to settle, detect + fix with retries");
Console.Error.WriteLine("  watch              (deprecated) Alias for settle-fix");
Console.Error.WriteLine();
Console.Error.WriteLine("Options:");
Console.Error.WriteLine("  --force            Run NVAPI reconfiguration even if MPO looks correct");
Console.Error.WriteLine("  --dry-run          Show what would happen without making changes");
Console.Error.WriteLine("  --delay=N          Settle delay in seconds before first check (default: 15)");
Console.Error.WriteLine("  --retries=N        Max retry attempts (default: 3)");
Console.Error.WriteLine();
Console.Error.WriteLine("Exit codes: 0=ok, 1=error, 2=fixed, 3=unresolved");
return EXIT_ERROR;
