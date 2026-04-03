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

3. **Smart** — Only triggers reconfiguration if MPO is on the wrong display. No unnecessary flicker.

## Usage

```
mpofix status          # Show per-display MPO plane assignment
mpofix fix             # Fix MPO if on wrong display (skips if already correct)
mpofix fix --force     # Always trigger reconfiguration
mpofix fix --dry-run   # Show what would happen without making changes
```

### Example Output

```
Display        Monitor              RGB   YUV   Primary   MPO
--------------------------------------------------------------------
\\.\DISPLAY1   27GL850              6     6               YES
\\.\DISPLAY2   LG ULTRAGEAR+        1     1     YES       no

MISMATCH: MPO on 27GL850, but primary is LG ULTRAGEAR+
Run 'mpofix fix' to reassign.
```

After fix:
```
Display        Monitor              RGB   YUV   Primary   MPO
--------------------------------------------------------------------
\\.\DISPLAY1   27GL850              1     1               no
\\.\DISPLAY2   LG ULTRAGEAR+        3     3     YES       YES

OK: MPO active on primary display (LG ULTRAGEAR+)
```

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
dotnet run -- status
```

## How It Detects MPO

Uses Windows D3DKMT (Direct3D Kernel Mode Thunk) functions from `gdi32.dll`:

- `D3DKMTOpenAdapterFromGdiDisplayName` — opens adapter handle per display
- `D3DKMTGetMultiPlaneOverlayCaps` — queries `MaxRGBPlanes` per VidPnSource
- Display with `MaxRGBPlanes > 1` has MPO active

Monitor names are resolved via Windows CCD API (`QueryDisplayConfig` + `DisplayConfigGetDeviceInfo`) for actual EDID names instead of "Generic PnP Monitor".

## Requirements

- Windows 10/11
- NVIDIA GPU with multi-monitor setup
- .NET 10 runtime
- No admin privileges required

## License

MIT
