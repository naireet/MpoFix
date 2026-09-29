# MpoFix

A Windows 10/11 CLI utility for detecting and attempting to repair an observed NVIDIA Multi-Plane Overlay (MPO) assignment issue on multi-monitor setups.

MpoFix discovers active outputs dynamically and targets the **current Windows primary display**. It is not hardcoded to LG monitors, any model, resolution, or Display Stream Compression (DSC) configuration. Monitor names in examples are illustrative, not a compatibility list.

## What It Addresses

On some NVIDIA multi-monitor configurations, the display reporting multiple available overlay planes can change after:
- Sleep/wake cycles
- Virtual display creation/removal by game-streaming software
- Display configuration changes

MpoFix was developed for cases where this appeared to affect presentation on the intended gaming display, and reapplying the display configuration restored the expected plane counts. This is **not** a universal rule that only one display can support MPO, nor a guarantee of smoother games on every GPU, driver, or display topology.

MPO is not a prerequisite for every Independent Flip or VRR/G-Sync path. Those can work without MPO; an application's presentation mode also depends on its swapchain, window configuration, overlays, Windows, and the driver. MpoFix's status is not proof that a particular game is using MPO, Independent Flip, or G-Sync. See Microsoft's [DXGI flip-model guidance](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/for-best-performance--use-dxgi-flip-model).

## How It Works

1. **Detection** — Enumerates active outputs and queries `D3DKMTGetMultiPlaneOverlayCaps` per display source. `MaxPlanes > 1` means Windows reports multiple planes currently available. Names are resolved through Windows CCD APIs (`QueryDisplayConfig` and `DisplayConfigGetDeviceInfo`) using monitor EDID names when available.

2. **Decision** — Treats the output with the highest `MaxPlanes` above 1 as the MPO holder and compares it with the Windows primary. Ties select the first enumerated output. No detected holder produces `Unknown`, not a confirmed mismatch. This heuristic is useful for the observed issue, not a general per-application presentation diagnostic.

3. **Repair** — Uses [NvAPIWrapper](https://github.com/falahati/NvAPIWrapper) to read the existing display paths and reapply **the same configuration** with `NvAPI_DISP_SetDisplayConfig` and `DriverReloadAllowed | ForceModeEnumeration`. After a 1.5-second wait, it checks whether the detected holder is primary.

The repair does not set `OverlayTestMode`, disable MPO in the registry, toggle DSC, choose a new display mode, replace EDID, or arbitrarily assign planes to a named monitor. It does not change which monitor Windows considers primary.

**Displays may briefly flicker during repair. Avoid running repairs while gaming or streaming.** Ordinary `fix` skips reconfiguration when detection is already `OnPrimary`; `fix --force` and the final fallback in `settle-fix` can reconfigure even when detection is uncertain.

### Why `MaxPlanes`, Not `MaxRGBPlanes`?

Microsoft defines [`MaxPlanes`](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/d3dkmthk/ns-d3dkmthk-_d3dkmt_get_multiplane_overlay_caps) as the total number of planes currently supported. Recent NVIDIA drivers have been observed reporting `MaxRGBPlanes` at the same fixed capability ceiling on every display, even when `MaxPlanes` differs. `MaxRGBPlanes` is therefore not a reliable assignment indicator for this issue. The `RGB` and `YUV` columns remain informational.

## Requirements and Installation

- Windows 10/11 with an NVIDIA GPU and a multi-monitor setup
- .NET 10 runtime for a framework-dependent deployment
- .NET 10 SDK only when building/publishing from source
- No administrator privileges required to run MpoFix

For automation, publish once and use a stable installed executable, **not `dotnet run`**. The latter needs the SDK and source tree and can build on each invocation.

From the repository directory, publish for 64-bit Windows:

```text
dotnet publish .\MpoFix.csproj -c Release -r win-x64 --self-contained false -o .\publish
```

Copy the **entire contents** of `publish` to a permanent directory such as `C:\Tools\MpoFix`. Keep the DLLs, runtime configuration, and other published files beside `mpofix.exe`; copying only the EXE is not sufficient. The paths below are examples you can replace.

Alternatively, replace `--self-contained false` with `--self-contained true` to include the .NET runtime. This produces a larger deployment but removes the separate runtime installation requirement; keep the whole publish folder in either case. See Microsoft's [.NET deployment guide](https://learn.microsoft.com/en-us/dotnet/core/deploying/).

## Usage

From the installed directory:

| Command | Behavior |
| --- | --- |
| `.\mpofix.exe status` | Show detected displays and plane counts; makes no display changes. Also the default with no arguments. |
| `.\mpofix.exe fix --dry-run` | Preview the repair path without reapplying the display configuration. |
| `.\mpofix.exe fix` | Skip if already `OnPrimary`; otherwise attempt repair, including when detection is `Unknown`. |
| `.\mpofix.exe fix --force` | Reapply the configuration even if detection says it is already correct. |
| `.\mpofix.exe settle-fix --delay=15 --retries=3` | Wait, check, and retry, then attempt a forced repair if still unresolved. |

**Run `status` and `fix --dry-run` before enabling automation**, then try a real repair only when display flicker is acceptable. `--dry-run` and `--force` are parsed only by `fix`. In particular, **`settle-fix --dry-run` does not provide a dry run**: the flag is ignored and a real repair can occur.

### Settle and Retry Behavior

`settle-fix` defaults to `--delay=15 --retries=3`; both values are integers clamped to at least 1. It waits the initial delay, then makes up to three detection attempts:

- `OnPrimary`: exit without reconfiguring.
- `OffPrimary`: wait 5 seconds and check again. Skip if now `OnPrimary`; otherwise attempt a repair, even if the second check becomes `Unknown`.
- `Unknown` or a failed repair: retry after a backoff of `delay × attempt` seconds, unless this was the last attempt.

After all attempts are exhausted, it makes **one additional forced repair attempt**, even if every check was `Unknown`. It is not a purely conditional or harmless monitoring command.

With defaults, the initial wait and backoffs total 60 seconds if all attempts are used (`15 + 15 + 30`). Mismatch confirmations add 5 seconds each, and applied repairs add 1.5-second verification waits; detection and driver calls take additional time. This is bounded by attempt count, **not a strict 60-second timeout**. It exits after the sequence rather than watching continuously.

The old `watch` command is deprecated; use `settle-fix` for new automation.

### Example Output

Illustrative `status` output; display numbering, names, and counts vary:

```text
Display        Monitor              Planes RGB   YUV   Primary   MPO
----------------------------------------------------------------------------
\\.\DISPLAY1   27GL850              6      5     5               YES
\\.\DISPLAY2   LG ULTRAGEAR+        1      5     5     YES       no

MISMATCH: MPO on 27GL850, but primary is LG ULTRAGEAR+
Run 'mpofix fix' to reassign.
```

After a successful repair on this example configuration:

```text
Display        Monitor              Planes RGB   YUV   Primary   MPO
----------------------------------------------------------------------------
\\.\DISPLAY1   27GL850              1      5     5               no
\\.\DISPLAY2   LG ULTRAGEAR+        6      5     5     YES       YES

OK: MPO active on primary display (LG ULTRAGEAR+)
```

Here, `MPO: YES` and the word "active" reflect the detector's plane-count heuristic, not measured game presentation.

### Exit Codes and Logs

| Code | Meaning in the current CLI |
| --- | --- |
| `0` | `status` completed, or `settle-fix` found `OnPrimary` and skipped repair. `status` returns 0 even when it prints a mismatch or "No displays found"; read its output. |
| `1` | `fix` failed (including failed verification), or an unrecognized command was supplied. |
| `2` | `fix` returned success, including an already-correct no-op or successful dry run; or `settle-fix` returned success from a repair attempt. It does not always mean a change occurred. |
| `3` | `settle-fix` remained unresolved after its forced fallback. |

`settle-fix` and deprecated `watch` append timestamped progress and outcomes to `%LOCALAPPDATA%\mpofix.log` for the account running the task. This is **not a full console transcript**: detailed detector/NVAPI output and final status tables are console-only. `status` and `fix` do not write this log. Log writes are best-effort, so an unwritable location may leave no file.

Task Scheduler can display application exit code 2 as **`0x2`**. When the action actually launched MpoFix, that is MpoFix's success result, not a generic failure. Check Task History and the latest log entry to distinguish it from a scheduler launch/path error. Treat `0` and `2` as successful MpoFix outcomes, with the command-specific caveats above.

## Automation with Windows Task Scheduler

Use an event-driven task rather than a frequent polling loop. Only enable automatic repairs after confirming the tool is useful on your setup; it does not detect whether a game or stream is active.

### Create the Task

Open **Task Scheduler**, select **Task Scheduler Library**, and choose **Create Task** (not Create Basic Task). Name it `MpoFix`.

On **General**, select the Windows account that owns the interactive desktop, choose **Run only when user is logged on**, and leave **Run with highest privileges** unchecked. Do not use SYSTEM, a service account, or a Session 0 task: the repair is intended for your interactive displays, not a background service desktop.

On **Actions**, create a **Start a program** action:

| Field | Example value |
| --- | --- |
| Program/script | `C:\Tools\MpoFix\mpofix.exe` |
| Add arguments | `settle-fix --delay=15 --retries=3` |
| Start in | `C:\Tools\MpoFix` |

Use the absolute executable path, keep arguments in their separate field, and enter the directory in **Start in** without quotes.

### Choose Triggers

On **Triggers**, add **At log on**, selecting **Specific user** and the same account as above.

For wake handling, add **On an event** with a **Basic** event filter:

| Field | Wake-event example |
| --- | --- |
| Log | `System` |
| Source | `Microsoft-Windows-Power-Troubleshooter` (may display as `Power-Troubleshooter`) |
| Event ID | `1` |

**Verify this event on your own system** in Event Viewer > Windows Logs > System after a normal wake; check its provider and message. This is an example, not a universal wake trigger. Modern Standby, hardware, and Windows versions can produce different events or timing.

A locked desktop can still have a logged-on user, so a wake event may start the task before unlock. That does not guarantee the displays are ready: detection can remain `Unknown`, and bounded retries eventually reach the forced fallback. If wake handling is unreliable, consider **On workstation unlock** for the specific user as a separate alternative trigger. Do not equate unlock with wake or stack redundant triggers unnecessarily. MpoFix already supplies its own settling delay.

### Settings and Conditions

On **Settings**, enable **Allow task to be run on demand** and set **If the task is already running, then the following rule applies** to **Do not start a new instance**. This prevents overlapping runs of this task, not separate manual MpoFix processes or other task definitions.

Leave trigger repetition and automatic restart-on-failure disabled: `settle-fix` already retries, and exit code 2 is a successful application result. Avoid short-interval schedules that might reconfigure displays during a game or stream.

On **Conditions**, leave **Wake the computer to run this task** unchecked; the task should respond to a wake, not cause one. Choose AC/battery restrictions to suit your machine and remember those conditions can prevent execution. Do not require the computer to be idle if you want the logon/wake action to run promptly.

### Check or Remove the Task

Save the task. At a convenient time when you are not gaming or streaming, right-click it and choose **Run**. This performs a real repair if needed and may flicker the displays. A console window may appear because the task runs in your interactive session.

After it finishes, check **Last Run Result**, `%LOCALAPPDATA%\mpofix.log`, and the task's **History** tab (enable **All Tasks History** if needed). Run `status` manually to inspect the current counts. A missing log can mean the process never started; check the published files, runtime, task account, action path, and conditions before assuming a display problem.

To stop future automation, right-click the task and choose **Disable**, or **Delete** to remove it. Neither changes driver settings or registry values, and neither reverses a repair already applied. Disabling future launches does not cancel a run already in progress.

Microsoft references: [task triggers](https://learn.microsoft.com/en-us/windows/win32/taskschd/task-triggers), [interactive logon type](https://learn.microsoft.com/en-us/windows/win32/api/taskschd/ne-taskschd-task_logon_type), [task security contexts](https://learn.microsoft.com/en-us/windows/win32/taskschd/security-contexts-for-running-tasks), [instance policies](https://learn.microsoft.com/en-us/windows/win32/api/taskschd/ne-taskschd-task_instances_policy), and [WakeToRun](https://learn.microsoft.com/en-us/windows/win32/taskschd/taskschedulerschema-waketorun-settingstype-element).

### Optional: Stream-End Integration

Only request a repair **after the full host streaming session has ended, virtual displays have been torn down, and the intended physical primary display has returned**. A brief client disconnect while the host session persists is not that event. Sunshine, Apollo, Vibepollo, Steam, and community tooling differ in hook support and teardown order; there is no universal copy-paste hook.

Where your host provides a suitable post-teardown hook, it can request the existing interactive task instead of directly waiting for MpoFix:

```text
schtasks /run /tn "MpoFix"
```

[`schtasks /run`](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/schtasks-run) requests a separate task run; it does not wait through MpoFix's settling and repair sequence. Its own result concerns the launch request, not the eventual repair. The calling account must have permission to run the task, and the configured desktop user must still be logged on. Do not switch the repair task to SYSTEM to work around permissions.

Keep this integration best-effort and outside the stream lifecycle's critical path: use the host's asynchronous/non-blocking hook mechanism, do not wait for repair completion, and do not make stream startup or teardown conditional on a successful launch or repair. A synchronous script ending with `exit 0` still waits for everything before that line; it is not asynchronous. If your host cannot isolate the hook this way, use the logon/wake task alone.

## License

MIT
