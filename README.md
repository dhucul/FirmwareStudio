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
- **READ BUFFER (0x3C)** in the plain "data" mode (mode 2) exposes only the controller's scratch buffer — often
  nothing. In **mode 6** (download-microcode-with-offsets) it instead addresses the firmware **flash** on
  MediaTek/Lite-On drives — the same region the official firmware updater reads — which is the strongest
  software route to a real firmware image on Lite-On iHAS/iHBS drives. Still read-only: `0x3C` is data-in.
- **MediaTek "read cache" (0xF1)** reads the controller's internal DRAM buffer. On many MediaTek drives
  this is the **disc-data cache**, which reads back empty when idle with no media — it is *not* the
  firmware ROM. On some generations it may contain resident firmware code. Either way it is never a
  guaranteed byte-exact ROM.
- **Renesas/NEC drives** (NEC ND-*, Optiarc AD-*, and NEC-based Lite-On iHAS revisions) expose their flash
  through the vendor commands **`0xCC` ReadRAM / `0xCD` ReadBoot** with no unlock — the binflash approach,
  which this tool ports. This is the reliable software route for that whole family.
- **MediaTek MT18xx Lite-On drives** (e.g. older iHAS revisions, the Xbox-360 Lite-On drives) gate firmware
  behind a controller **"Vendor Mode"** (status `0x70`) entered via a **timed physical power-cycle** plus
  **PortIO / raw ATA register** access (a kernel driver, often on VIA-6421-class hardware — normal AHCI
  freezes). FirmwareStudio can drive raw ATA where the OS allows it (`IOCTL_ATA_PASS_THROUGH_DIRECT`, see
  the smoke's *ATA pass-through* line), but many optical drives' drivers **refuse raw ATA** entirely
  (`ERROR_NOT_SUPPORTED`), and the vendor-mode dance can't be reproduced by a clean pass-through tool anyway.
  For these drives use MtkWinFlash / JungleFlasher / DosFlash (they ship the PortIO driver), or a programmer.
- **UHD Blu-ray drives** expose firmware via a service ("svc") mode on specific supported models
  (LibreDrive/MakeMKV territory). Detect-only in v1.

Bottom line: a true, byte-exact firmware-ROM backup often requires a **hardware SPI/flash programmer**.
FirmwareStudio surfaces this instead of pretending otherwise, and labels every dump honestly.

## Extraction methods (v1)

**Software (SCSI) — the "Optical drive" tab:**

| Method | Command | Applies to | Notes |
|--------|---------|-----------|-------|
| Universal READ BUFFER probe | `0x3C` mode 2 | any drive | Always safe. Reads whatever the controller's scratch buffer exposes. |
| **MediaTek/Lite-On flash read** | `0x3C` mode 6 | MediaTek (Lite-On iHAS/iHBS) | Reads the firmware **flash** via the download-microcode-with-offsets addressing the official Lite-On/MediaTek updater uses (same read redumper's MT1959 flasher issues). The best software path for a real firmware image. Read-only — `0x3C` is data-in; the write/save microcode modes are `0x3B` WRITE BUFFER, never issued. |
| MediaTek internal cache read | `0xF1` | MediaTek chipsets | Reads controller DRAM disc-cache (may hold firmware code; often empty when idle). Auto-**de-mirrors**: the controller aliases a small unique region (e.g. ~1 MiB) across the whole address window, so the read stops once the repeat is detected and keeps only the unique copy instead of an 8× mirror. |
| PLDS/Lite-On vendor read | `0xDF` | PLDS/Plextor/Lite-On | The Plextor/PLDS updater's vendor command; walks drive-state/RAM buffers. |
| **NEC/Renesas RAM read** | `0xCC` / `0xCD` | Renesas/NEC (NEC ND-*, Optiarc AD-*, NEC-based Lite-On iHAS) | Ported from **binflash**. Identifies the drive from its firmware signatures, then dumps the model's flash range(s) — no unlock needed. Read-only: only the data-in `ReadRAM`/`ReadBoot` opcodes, never the `0xCC` erase/safe-mode sub-modes or `0xCB` WriteRAM. Some newer drives gate the full flash behind a "safe mode" state change this tool does not issue; those dump partially and are labelled as such. |
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

## Firmware update-image analysis (`.1KN` / `.1JN`)

The vendor's own **firmware update file** (the payload the official updater extracts from its
password-protected ZIP — password `Vinpower13059881` — and flashes) *is* the byte-exact flashable ROM. FirmwareStudio can open and
characterise one without any drive — **"Analyze firmware file…"** on the Optical-drive tab, or headless:

```
dotnet run --project tools/FirmwareStudio.Smoke -- fwfile <image.1KN>
```

`FirmwareImage.Parse` reads the PLDS/Lite-On **VPD wrapper** at the exact offsets the vendor updater uses
(magic @0, `VPD_update_file` @0x400, model @0x414, build-date @0xE25A4, version @end−4), then classifies the
image into regions by Shannon entropy (header / encrypted body / config-tables / padding), extracts strings,
and — for the high-entropy body — detects an **ECB-mode block cipher** from repeated 16-byte ciphertext
blocks. It labels the result honestly: on the Plextor PX-891SAF PLUS (MediaTek MT62SA) the ~700 KB body is
ECB-encrypted with the **key resident in the controller** — the PC updater flashes it verbatim and the drive
decrypts internally, so this is *not* a plaintext ROM, and there is no PC-side decryptor to make it one.

> **Why there's no software "read the ROM back" for this drive:** decompiling the official
> `891SAFPLUSPCDriveUpdater` confirms it issues **no full-flash read** — every read is ≤12 bytes of
> drive-state/status via `0xDF` (register banks), plus 4-byte `0x3C` status polls during flashing.
> Independently, reverse-engineering Vinpower's `vpscan`/QPxTool scanner and hardware-testing the full
> `0xDF` sub-command surface on the PX-891SAF PLUS shows the command's `byte[1]` is a **function selector**,
> and *every* function is quality-scan or servo, **none read firmware**: `82`=C1/C2/CU error counters,
> `1B`=jitter/beta, `08`=focus/tracking-error, `02`=head position, `A0`/`A3`=measurement start/stop,
> `97`=interval reset, `00`=register/state bank (the only one this tool uses). So `0xDF` genuinely exposes
> no firmware-read path. A byte-exact on-drive dump would need a read command the firmware doesn't expose, or
> hardware SoC access. The `0xF1` cache read still surfaces *decrypted-but-sparse* firmware as loaded in RAM.

## Project layout

```
src/FirmwareStudio.Core   — logic + SCSI interop (net10.0, x64)
  Scsi/        Native.cs, ScsiDevice.cs, ScsiCommand.cs, SenseData.cs, ScsiResult.cs
  Drives/      DriveEnumerator.cs, DriveIdentifier.cs, ChipsetDetector.cs
  Extraction/  IFirmwareExtractionMethod + 6 methods (incl. NecRenesasReadMethod + NecDriveTable), ExtractionOrchestrator, DumpWriter
  Analysis/    DumpAnalyzer.cs (dump region-map + mirror/alias detection + string extraction)
  Firmware/    FirmwareImage.cs (.1KN/.1JN VPD-wrapper parser + entropy/ECB classification)
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
