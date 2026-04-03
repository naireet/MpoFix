using MpoFix;

if (args.Length == 0 || args[0] is "status" or "-s")
{
    MpoDetector.PrintStatus();
    return 0;
}

if (args[0] is "fix" or "-f")
{
    bool dryRun = args.Contains("--dry-run");
    bool force = args.Contains("--force");

    Console.WriteLine("=== Before ===");
    MpoDetector.PrintStatus();
    Console.WriteLine();

    bool ok = MpoFixer.Fix(dryRun: dryRun, force: force);
    if (!ok) return 1;

    if (!dryRun)
    {
        Thread.Sleep(1500);
        Console.WriteLine();
        Console.WriteLine("=== After ===");
        MpoDetector.PrintStatus();
    }

    return 0;
}

if (args[0] is "watch" or "-w")
{
    int interval = 10;
    var intervalArg = args.FirstOrDefault(a => a.StartsWith("--interval="));
    if (intervalArg is not null && int.TryParse(intervalArg.Split('=')[1], out int parsed))
        interval = Math.Max(2, parsed);

    Console.WriteLine($"Watching MPO assignment every {interval}s. Press Ctrl+C to stop.");
    MpoDetector.PrintStatus();
    Console.WriteLine();

    while (true)
    {
        Thread.Sleep(interval * 1000);

        if (!MpoDetector.IsMpoOnPrimary())
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] MISMATCH detected — fixing...");
            if (MpoFixer.Fix())
            {
                Thread.Sleep(1500);
                MpoDetector.PrintStatus();
            }
            Console.WriteLine();
        }
    }
}

Console.Error.WriteLine("Usage: mpofix [status|fix|watch] [options]");
Console.Error.WriteLine();
Console.Error.WriteLine("Commands:");
Console.Error.WriteLine("  status           Show per-display MPO plane assignment");
Console.Error.WriteLine("  fix              Fix MPO if on wrong display (skips if already correct)");
Console.Error.WriteLine("  watch            Monitor and auto-fix MPO assignment continuously");
Console.Error.WriteLine();
Console.Error.WriteLine("Options:");
Console.Error.WriteLine("  --force          Run NVAPI reconfiguration even if MPO looks correct");
Console.Error.WriteLine("  --dry-run        Show what would happen without making changes");
Console.Error.WriteLine("  --interval=N     Watch polling interval in seconds (default: 10)");
return 1;
