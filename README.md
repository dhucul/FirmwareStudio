# FirmwareStudio

A Windows tool for reading an optical drive's **own firmware** (the microcode/RAM/flash inside a
CD/DVD/Blu-ray drive) over SCSI pass-through. For firmware backup, analysis, drive modding, and
reverse engineering. C#/.NET 10 + WPF, themed to match DisasmStudio / StatStudio.

> **Read-only by design.** FirmwareStudio never writes to or flashes a drive. There is no WRITE BUFFER
> and no flash-write command anywhere in the code.

## Honest limitations (read this first)

There is **no universal "read the firmware flash" SCSI command.** What you can extract depends entirely
on the drive's chipset, and many drives expose nothing useful over software:

- **INQUIRY** gives only the firmware *revision string*, never the image.
- **READ BUFFER (0x3C)** exposes controller buffers on *some* drives — often nothing.
- **MediaTek "read cache" (0xF1)** reads the controller's internal DRAM buffer. On many MediaTek drives
  this is the **disc-data cache**, which reads back empty when idle with no media — it is *not* the
  firmware ROM. On some generations it may contain resident firmware code. Either way it is never a
  guaranteed byte-exact ROM.
- **UHD Blu-ray drives** expose firmware via a service ("svc") mode on specific supported models
  (LibreDrive/MakeMKV territory). Detect-only in v1.

Bottom line: a true, byte-exact firmware-ROM backup often requires a **hardware SPI/flash programmer**.
FirmwareStudio surfaces this instead of pretending otherwise, and labels every dump honestly.

## Extraction methods (v1)

**Software (SCSI) — the "Optical drive" tab:**

| Method | Command | Applies to | Notes |
|--------|---------|-----------|-------|
| Universal READ BUFFER probe | `0x3C` | any drive | Always safe. Reads whatever the controller exposes. |
| MediaTek internal cache read | `0xF1` | MediaTek chipsets | Reads controller DRAM (firmware code and/or disc cache; may be empty). |
| UHD Blu-ray service mode | — | supported UHD models | Detection only in v1. |

**Hardware (SPI flash) — the "Hardware" tab:** reads the drive's flash chip *directly*, bypassing the
controller, when the software methods can't. Self-contained (no external tools) via a **CH341A** USB
adapter + a SOIC-8 test clip:

- **Detect adapter** → opens the CH341A (needs the WCH CH341PAR driver / `CH341DLLA64.dll`).
- **Identify chip** → JEDEC `RDID (0x9F)` → manufacturer + size (capacity byte = log2(size)).
- **Read flash** → `READ (0x03)` in 4 KB blocks over SPI → full ROM to `.bin` + JSON sidecar.

Only works if the firmware is in an *external* SPI NOR flash (not inside the controller MCU). **Voltage
warning:** the flash is 3.3 V; many CH341A clones output 5 V and can destroy the chip — use a 3.3 V-safe
adapter. This path requires the physical adapter, so it is untested in CI; it degrades gracefully (a clear
"install the driver / no adapter" message) when no adapter is present.

## Project layout

```
src/FirmwareStudio.Core   — logic + SCSI interop (net10.0, x64)
  Scsi/        Native.cs, ScsiDevice.cs, ScsiCommand.cs, SenseData.cs, ScsiResult.cs
  Drives/      DriveEnumerator.cs, DriveIdentifier.cs, ChipsetDetector.cs
  Extraction/  IFirmwareExtractionMethod + 3 methods, ExtractionOrchestrator, DumpWriter
  Models/      DriveIdentity, ChipsetInfo, ExtractionResult, …
  Logging/     IScsiLogger, CommandLogEntry, FileAndMemoryLogger
src/FirmwareStudio.Wpf    — GUI (net10.0-windows, WinExe, requires admin)
tools/FirmwareStudio.Smoke — headless self-test console
```

The SCSI layer is a C# P/Invoke port of the pass-through code in the native OptiScan project.

## Build & run

```
dotnet build FirmwareStudio.slnx -c Debug
```

Run the GUI **as administrator** (SCSI pass-through needs elevation; the manifest forces the UAC prompt):

```
src/FirmwareStudio.Wpf/bin/Debug/net10.0-windows/FirmwareStudio.exe
```

In the app: pick a drive → **Identify** (shows vendor/model/firmware/serial/bus + detected chipset and
which methods apply) → choose a method (or Auto) → **Extract firmware** → **Save dump…**. Every SCSI
command is shown in the command-log pane; the hex pane previews the dump. Saving writes `<stem>.bin`,
a `<stem>.json` metadata sidecar, and a `<stem>.log` command log.

## Verification

```
# Elevated console self-test: struct-layout ABI, enumeration, INQUIRY, chipset, READ BUFFER walk,
# MediaTek cache sample, orchestrator + DumpWriter round-trip.
dotnet run --project tools/FirmwareStudio.Smoke

# Same checks headless from the GUI exe:
FirmwareStudio.exe --smoke-scsi
```

A successful run enumerates drives, identifies them, detects the chipset, and either produces a dump or
clearly reports why the drive is unsupported (e.g. "needs a hardware programmer").

## Safety

- Strictly read-only: no `0x3B` WRITE BUFFER, no flash-write opcodes.
- Vendor commands are gated behind chipset detection and use tiny/zero allocation lengths.
- Bounded command timeout + cancellation; every CDB is logged before it is issued.
- Requires administrator (enforced by the app manifest).
