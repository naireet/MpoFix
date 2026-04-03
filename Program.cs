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

Console.Error.WriteLine("Usage: mpofix [status|fix] [options]");
Console.Error.WriteLine();
Console.Error.WriteLine("Commands:");
Console.Error.WriteLine("  status           Show per-display MPO plane assignment");
Console.Error.WriteLine("  fix              Fix MPO if on wrong display (skips if already correct)");
Console.Error.WriteLine();
Console.Error.WriteLine("Options:");
Console.Error.WriteLine("  --force          Run NVAPI reconfiguration even if MPO looks correct");
Console.Error.WriteLine("  --dry-run        Show what would happen without making changes");
return 1;
