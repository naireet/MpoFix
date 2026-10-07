# MpoFix

A Windows CLI tool that detects and fixes NVIDIA Multi-Plane Overlay (MPO) plane assignment on multi-monitor setups.

## The Problem

On NVIDIA GPUs with multiple monitors, MPO planes are assigned to only **one display at a time**. This is required for Independent Flip in borderless windowed mode, which enables VRR/G-Sync. Without it, games won't feel smooth when framerate fluctuates.

The MPO assignment can randomly migrate to the wrong display after:
- Sleep/wake cycles
- Virtual display creation/removal (game streaming software)
- Display configuration changes
- Alt-tabbing with overlays

The typical fix is manually toggling the primary monitor in NVIDIA App — this tool automates that.

## How It Works

1. **Detection** — Queries `D3DKMTGetMultiPlaneOverlayCaps` (the same kernel-mode thunk API that [Special K](https://github.com/SpecialKO/SpecialK) uses) to read per-display MPO plane assignment with actual EDID monitor names.

2. **Fix** — Uses [NvAPIWrapper](https://github.com/falahati/NvAPIWrapper) to call `NvAPI_DISP_SetDisplayConfig` with force flags, triggering a driver-level display path reconfiguration that reassigns MPO planes — the same mechanism as toggling primary in NVIDIA App.

3. **Legacy guard** — Skips reconfiguration when MPO is confirmed on primary.
   Legacy `fix` may still reconfigure on `Unknown`, and `settle-fix`/`watch` retain
   their final force fallback. The new wake path does **not** use that fallback.

## Usage

```
mpofix status          # Show per-display MPO plane assignment
mpofix fix             # Fix MPO if on wrong display (skips if already correct)
mpofix fix --force     # Always trigger reconfiguration
mpofix fix --dry-run   # Show what would happen without making changes
mpofix diagnose       # Read-only native/desktop/topology evidence; writes diagnostics only
mpofix wake-recover    # One diagnostic capture after 30s; actual recovery is blocked
mpofix wake-recover --delay=0 # Immediate diagnostic capture (no display writes)
mpofix wake-recover --restore-topology  # Explicit opt-in, STILL blocked (no display writes)
```

### Example Output

```
Display        Monitor              Planes RGB   YUV   Primary   MPO
----------------------------------------------------------------------------
\\.\DISPLAY1   27GL850              6      5     5               YES
\\.\DISPLAY2   LG ULTRAGEAR+        1      5     5     YES       no

MPO: OffPrimary (ConfirmedHolder)
MISMATCH: MPO off primary. Run 'mpofix fix' to reapply current NVIDIA config.
```

After fix:
```
Display        Monitor              Planes RGB   YUV   Primary   MPO
----------------------------------------------------------------------------
\\.\DISPLAY1   27GL850              1      5     5               no
\\.\DISPLAY2   LG ULTRAGEAR+        6      5     5     YES       YES

MPO: OnPrimary (ConfirmedHolder)
```

### Exit codes and compatibility

`0` = OK, `1` = error (including diagnostic persistence failure), `2` = fixed,
`3` = unresolved/skip. `status` retains exit `0` even for unknown assignment.
Legacy `fix` retains exit `2` on successful dry-run/already-correct checks.
`settle-fix` defaults remain 15 seconds/3 attempts, 5-second mismatch confirmation,
linear delay backoff and a final force attempt; deprecated `watch` retains its
timeout mapping. Legacy repair retains `DriverReloadAllowed | ForceModeEnumeration`,
the **current** NVIDIA config, and 1500 ms post-write verification wait.
The existing stream-end invocation below is unchanged.

`diagnose` returns `0` for complete accessible CCD/desktop/MPO observations
(including idle MPO Unknown), `3` for incomplete/inaccessible native evidence,
and `1` on errors. Optional unsupported/unavailable color evidence stays unknown
without converting an otherwise valid CCD layout query to failure.
It does not repair anything. `wake-recover` returns `3` **with or without**
`--restore-topology`, or `1` on errors. Wake rejects legacy flags such as `--force`.
No opt-in enables production restoration in this version.
The wake-only delay defaults to 30 seconds and accepts integers from 0 to 60.
There is one capture after that wait; no cached pre-wait eligibility, recurring
worker, native repair attempt or force fallback. Input-desktop/query failure logs
an explicit skip. Otherwise, Local/app/ownership/baseline evidence is reported
as **unknown/unavailable**, not guessed from MPO, connected monitors or transport
counts. Invalid, duplicate or legacy wake options return `1` before native queries.

## Automation

Add to your stream-end script to auto-fix after virtual display teardown:

```powershell
# Wait for display to settle after stream disconnect
Start-Sleep -Seconds 4
# Fix MPO assignment
& dotnet run --project "path\to\MpoFix" -- fix
```

## Building

Requires .NET 10 SDK.

```
dotnet build
dotnet test .\Tests\MpoFix.Tests.csproj
```

Tests are hermetic: fake CCD/D3DKMT/NVAPI/desktop/clock boundaries and pure
ownership/layout decisions only; there is no host integration to exercise.
Test-owned log files are created under the test output directory and removed
after each test; no Win32 or NVIDIA adapter is instantiated. Do **not** run
`status`, `fix`, `settle-fix`, `watch`, or `diagnose` as build/verification smoke
tests on a host. This work has not been deployed or validated against live displays.

## How It Detects MPO

Uses Windows D3DKMT (Direct3D Kernel Mode Thunk) functions from `gdi32.dll`:

- `D3DKMTOpenAdapterFromGdiDisplayName` — opens adapter handle per display
- `D3DKMTGetMultiPlaneOverlayCaps` — queries `MaxPlanes` per VidPnSource
- Display with `MaxPlanes > 1` has MPO active (the plane count Special K reads).
  Note: `MaxRGBPlanes` is **not** used — recent NVIDIA drivers pin it at a fixed
  capability ceiling on every display, so it no longer indicates the active display.

Monitor names are resolved via Windows CCD API (`QueryDisplayConfig` + `DisplayConfigGetDeviceInfo`) for actual EDID names instead of "Generic PnP Monitor".

`Unknown` is not synonymous with “displays not ready.” Diagnostics distinguish:

- `NoSuccessfulQuery`: no display produced a successful adapter/caps query.
- `NoActiveHolder`: successful queries, but no `MaxPlanes > 1` holder (possibly idle).
- `IncompleteEvidence`: enumeration/query/close failure or missing primary evidence.
- `AmbiguousHolder`: more than one display shares the maximum plane count.

These results do not establish lost topology or the cause of an initial wake failure.
Failed per-display observations are retained, not silently dropped.

## Persistent native diagnostics

At runtime all commands append JSON lines to `%LOCALAPPDATA%\mpofix.log`, with
UTC timestamps, a per-process run correlation ID, category and escaped message.
The log is append-only; no rotation/installer/task configuration is added.
Logging failures are visible errors, never catch-and-ignore. Review log contents
before sharing: monitor names/device paths and configuration evidence are included.

Coverage includes:

- Enumeration completeness; per-display adapter open, caps and close **raw
  hexadecimal NTSTATUS** (only zero counts as success), plane counts and reasons.
- NVAPI initialize/get/set/unload exceptions with type/HResult, path count/flags,
  topology observations before/after a legacy current-config repair, and actual
  post-repair MPO verification. Reapply is not a saved-layout restore.
- One shared CCD collector using `QDC_ONLY_ACTIVE_PATHS`, with at most three
  buffer-race attempts (50 ms waits), limits of 256 paths/1024 modes, returned-count
  guards, and mode type/adapter/id/index validation (unsupported packed virtual
  mode indexes are rejected, not guessed). `DISPLAYCONFIG_MODE_INFO` is
  **64 bytes total**, union at offset 16 (largest member 48 bytes).
- Target path/name/EDID manufacturer/product **only when EDID IDs are valid**,
  name/path/status flags, adapter/source/target IDs, output technology, target
  availability, source XY/size, exact rational refresh, rotation and scaling.
- Read-only input desktop name (`OpenInputDesktop` with `DESKTOP_READOBJECTS`,
  `GetUserObjectInformationW/UOI_NAME`, `CloseDesktop`), preserving native errors.
  Access denied, another desktop (e.g. Winlogon), and unknown remain distinct.
  A `Default` name alone does not prove an unlocked/interactive local session;
  Windows can return the reconnect desktop for a disconnected session.

Active CCD paths do **not** inventory disconnected/inactive physical monitors.
Friendly names, GDI indices, device paths and adapter LUIDs do **not** establish
stable EDID serial/machine/adapter identity for restoration.

Read-only color diagnostics use the verified, pinned Microsoft SDK
[`wingdi.h`](https://raw.githubusercontent.com/microsoft/win32metadata/5c5efbc01d4c87f6830ec304d42777991d533154/generation/WinSDK/RecompiledIdlHeaders/um/wingdi.h)
ABI: `GET_ADVANCED_COLOR_INFO_2` request **15**, 36-byte structure (20-byte header;
flags/encoding/bits-per-channel/active-mode at offsets 20/24/28/32).
Report raw flags, encoding, bit depth, advanced-color support/active state,
policy limitation, HDR/WCG support and user preferences. **`HdrUserEnabled`
(bit 5) and `HdrActive` (active mode HDR=2) are separate**: a user-enabled
preference need not currently be active. Active modes SDR=0 and WCG=1 are not
HDR; an unknown enum value leaves `HdrActive` nullable unknown.

If INFO_2 returns `ERROR_NOT_SUPPORTED` (50) or `ERROR_INVALID_PARAMETER` (87),
attempt the legacy read-only `GET_ADVANCED_COLOR_INFO` request **9**, 32 bytes.
Retain both native error values and report legacy advanced-color support/enabled,
wide-color enforcement, policy force-disabled, encoding and bit depth when
available. Legacy advanced-color enabled can mean WCG: **never infer HDR**.
Other INFO_2 errors do not trigger fallback. Unsupported/failed color queries
keep HDR nullable unknown and persist errors/exceptions. Query headers identify
the actual CCD target adapter/id; no user-controlled request type is accepted.
Fake tests verify both ABIs, headers, flag meanings, preference versus active
mode, unsupported fallback and persistent errors. No color/HDR setters exist.
These diagnostics are not a trusted baseline or production recovery integration.

## Wake recovery: diagnostics implemented, physical restore blocked

**Benched by user decision on October 7, 2026:** keep the existing `MpoFixOnWake`
task and installed MPO executable. This source is preserved for later review,
not deployed. Physical wake restoration is unimplemented, not completed.

`wake-recover` is a **diagnostic prototype, not a usable physical-display repair**.
It logs one post-settle observation and an explicit non-success skip. No snapshot
store/importer, host API client, restorer, lease framework or recovery executor is
implemented. It cannot prove Local eligibility or emit a production `skip-healthy`
without the owner evidence below. Do not replace a working wake task with this
diagnostic-only command.

`WakeEligibility` is a small pure classification seam, not an authorization token.
Given a trusted latest confirmed Local baseline and authoritative owner state,
it distinguishes unchanged layout, mismatch, missing monitor and unknown
identity/fields. Active **and paused** apps, VDD ownership, transitions and unknown
host state skip. A known healthy Local layout requires no topology mutation even
when MPO is idle/Unknown. An extra physical monitor is not itself an error.
Exact comparison preserves primary, resolution, rational refresh, XY, HDR user
preference, rotation and scaling; unknown fields never become guessed defaults.
Even a fresh Local/mismatch result only says owner integration is required: it
does not authorize a write. There is no invented snapshot age cutoff.

### Why polling or existing maintenance APIs are insufficient

The official [Vibepollo 2.0.0 source](https://github.com/Nonary/Vibepollo/tree/8a8c4b03a280ab9f567beb380110abb80f5220b8)
has an internal `nvhttp::stream_lifecycle_mutex()` used by launch/resume before
display preparation (`src/nvhttp.cpp`, launch/resume route handlers). That gate
is not exposed as a safe external Local-restore operation.

- `/api/session/status` calls `proc::proc.running()`, which performs deferred
  launch/process lifecycle work and can call `terminate()`. It is not passive.
- `/api/clients/display-layout` refreshes the host's physical baseline on GET.
- `/api/display/terminate_virtual` invokes terminal cleanup that deliberately
  **overrides managed display ownership**, removes VDDs and stops watchdogs.
  It is not a safe local-wake recovery request.
- Sending helper `REVERT` IPC directly bypasses host admission. The internal
  `display_helper_integration::revert()` reports command dispatch, not an exact
  completed layout/HDR readback. Its helper-execution mutex alone does not exclude
  all earliest stream preparation.

None of these APIs/IPC messages are called here. A fresh process/RTSP check,
private wake mutex or fixed 30-second delay cannot prevent a stream starting
between eligibility and mutation. Users must not have to time stream launch
around wake recovery.

### Smallest remaining owner integration and acceptance criteria

Implement the one bounded Local-wake operation **inside the existing host display
lifecycle**, not as a competing CCD owner. After every settle wait, acquire the
same exclusion covering earliest launch/resume/VDD preparation, retained paused
apps, Remote Monitor ownership and teardown. Use side-effect-free retained app
state under that exclusion, plus the host's actual physical identity mapping and
latest confirmed Local layout. A normal input-desktop name is not by itself proof
of an interactive/unlocked session.

Existing `display_golden_restore.json` and `display_session_previous.json` are
evidence, not automatically an approved desired layout. A read-only October 7
inspection found different primary/positions in them; selecting the newer file
or blindly restoring golden would guess user intent. Establish which owner
baseline represents the latest user-confirmed Local layout, without capturing a
streaming/VDD layout or overwriting existing snapshots.

Under that same ownership: log local eligibility; exact healthy layout logs
`skip-healthy` with no topology change; only a confirmed mismatch with all
expected physical monitors available permits **one** existing-owner restore.
Verify exact primary, resolution, refresh rational, positions, rotation/scaling
and HDR preference/active-state evidence before existing MPO evaluation. MPO on
primary is no-op; Unknown logs uncertainty and never permits force; confirmed
off-primary may use current-config repair only while ownership is still held.
Read back layout and MPO afterward; failed/unknown verification is not success.

Regression acceptance must include stream launch during the initial delay,
between eligibility and restore, during restoration, and before MPO repair,
plus paused apps, VDD crashes, simultaneous multi-monitor streaming, inaccessible
desktop, missing/ambiguous physical identity and conflicting Local baselines.
Fresh checks are necessary but **not sufficient**: the owner must serialize or
cancel the entire operation, including the actual native writes. These live-owner
race/restore guarantees are **not established by the pure/fake tests in this repo**.
Bounded observations/waits do not preempt a hung synchronous native call.

### Review, rollout and rollback (no deployment performed)

- Review code and run the hermetic build/tests in an isolated worktree first.
- Keep production wake restoration disabled until the owner serialization,
  latest approved Local baseline, physical identity mapping and exact restore
  readback are implemented and verified. No process/RTSP workaround.
- Only under a separate explicit deployment approval: preserve previous binary
  and existing task command; stage read-only diagnostics and review evidence
  before any separately approved native restoration experiment.
- Do not deploy this prototype over `MpoFixOnWake` or replace the shared July 3
  binary. Future approved recovery should use an isolated staged binary and
  **one** wake entrypoint, preserving the existing task principal/protection.
  Leave monitor-idle and stream-end triggers/callers unchanged.
- Future approved rollback restores that previous binary/task command. Do not
  automatically restore a display layout, edit host snapshots, restart services,
  or change scheduler/startup/registry. No deployment or rollback scripts added.

### Native documentation used

- [QueryDisplayConfig and buffer races/errors](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-querydisplayconfig)
- [Mode union](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_mode_info),
  [signal ABI](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_video_signal_info),
  [source mode/position](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_source_mode),
  [target path/index ABI](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_path_target_info)
- [Target name/EDID fields](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_target_device_name),
  [name flags](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_target_device_name_flags)
- [OpenInputDesktop](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-openinputdesktop),
  [GetUserObjectInformationW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getuserobjectinformationw),
  [desktop access rights](https://learn.microsoft.com/en-us/windows/win32/winstation/desktop-security-and-access-rights)
- [D3DKMT open-adapter structure](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/d3dkmthk/ns-d3dkmthk-_d3dkmt_openadapterfromgdidisplayname)
- [Pinned Microsoft SDK advanced-color query declarations](https://raw.githubusercontent.com/microsoft/win32metadata/5c5efbc01d4c87f6830ec304d42777991d533154/generation/WinSDK/RecompiledIdlHeaders/um/wingdi.h)

## Requirements

- Windows 10/11
- NVIDIA GPU with multi-monitor setup
- .NET 10 runtime
- No admin privileges required

## License

MIT
