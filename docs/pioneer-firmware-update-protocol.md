# Pioneer optical-drive firmware update protocol

Reverse-engineered from Pioneer's **BDR-S13** updater `BDR-S13JBK_UBK_EBK_CBK_FW105EU.exe`
(firmware 1.05EU) — the packaging, image format, and the SCSI flash flow. This is a **reference
document**; FirmwareStudio implements only the **read-only** parts (see *Scope* at the end).

## 1. Packaging (three layers)

| Layer | Contents |
|---|---|
| Outer `.exe` (~2.9 MB) | **WinRAR ZIP-SFX** (sfxzip module), config `Silent=1 / TempMode / Setup=Updater.exe`. Silently extracts and runs the inner updater. The single ZIP entry `Updater.exe` is DEFLATE-compressed (method 8). |
| `Updater.exe` (~3.8 MB) | Pioneer "ODD Firmware Update Utility" (MFC console, built 2023-08-07). The firmware images are stored as PE resources of a **custom string type `"BINARY"`**, language `0x0411` (Japanese): **id 131 = kernel** part, **id 132 = normal** part. |
| Each `BINARY` resource | A Pioneer "microcode" image — a 512-byte plaintext header + a drive-decoded body (§2). |

`FirmwareStudio.Core.Firmware.PioneerFirmwareImage` parses all three forms (outer SFX, inner
`Updater.exe`, or an already-extracted `.bin`).

## 2. Microcode image format

A 512-byte (`0x200`) **plaintext ASCII header**, terminated by a Ctrl-Z (`0x1A`) EOF marker and
zero padding, followed by the body from `0x200`.

```
********  Copyright(c) 2000 Pioneer Corporation  ********
This is microcode file.
ID : PIONEER BD-RW   BDR-S13U
Revision Level  : 1.05
Hardware Version : SAT 9201
Kernel Version  : ID43
Destination     : ID43
File Type       : Kernel        (or "Normal")
Generated Date  : 23/08/01
Kernel Version2 : 0000
```

- **Machine-read fixed offsets** (what `Updater.exe` reads, not just the text lines):
  `+0x60` model string (`PIONEER BD-RW   BDR-S13U`), `+0x90` version major digit,
  `+0x92`/`+0x93` version minor (BCD → `1.05`), `+0x1F0` 16-byte part label `S920143{n}.105`
  (`n` = 0 kernel / 1 normal).
- **One image, all regions.** The JBK/UBK/EBK/CBK region variants are handled by one image plus a
  field, not four separate blobs.

### Body encoding (not decrypted on the PC)

The body from `0x200` is high-entropy (~7.99 bits/byte). Analysis (kernel vs normal differential,
ECB block-repetition, repeating-XOR cross-validation) shows a **position-based byte stream cipher
with a long, non-repeating keystream shared between the kernel and normal parts** — *not* ECB, not
standard compression, not a short repeating key. Crucially, `Updater.exe` performs **no PC-side
decode**: it `memcpy`s the raw resource into a work buffer, parses only the plaintext header, and
streams the body to the drive verbatim (§3). **The drive's own bootloader decrypts internally**;
the key is not on the PC. Recovering the plaintext would need a known-plaintext anchor (a decrypted
reference image or a second firmware version) — see the encoding analysis in the session notes.

## 3. SCSI flash flow (write path — NOT implemented by FirmwareStudio)

The updater flashes over **SPTI** (`CreateFileA \\.\X:` + `DeviceIoControl` /
`IOCTL_SCSI_PASS_THROUGH_DIRECT`), with a WNASPI32 (ASPI) fallback. **Fully recovered via Ghidra
headless decompilation** of `Updater.exe` (`FUN_004095E0` is the common WRITE BUFFER builder; the
capstone linear sweep had missed the register-indexed `0x3B` store — Ghidra's complete disassembly
found it at `0x0040960F`).

Every drive command is **`WRITE BUFFER (0x3B)`** through `FUN_004095E0`, a standard 10-byte CDB whose
`mode` (CDB[1]) and `buffer id` (CDB[2]) select the operation:

```
CDB layout (FUN_004095E0):
  [0] 0x3B                      WRITE BUFFER
  [1] mode                      CDB[1]
  [2] buffer id                 CDB[2]
  [3..5] buffer offset (BE24)   running offset
  [6..8] param list length (BE24, MSB forced 0 → ≤ 0xFFFF)
  [9] 0x00                      control
  + DATA-OUT payload; direction DATA_OUT when length != 0
```

| Step | Function | mode | buf id | len | Meaning |
|---|---|---|---|---|---|
| 1. INQUIRY (`0x12`) | `FUN_00408E60` | — | — | — | identify + compare vendor/model/revision to the image `ID` |
| **2. Enter kernel mode** | `FUN_0040A060` | **4** (download µcode) | **0xFF** | `0x100` | 256-byte control block → drive reboots into bootloader; then `Sleep(2000)` + `Sleep(1000)` |
| 3. Verify kernel mode | `FUN_00404DD0` / `FUN_0040A4E0` | INQUIRY | — | — | re-INQUIRY; kernel mode confirmed when the model reads the bootloader identity **`PIONEER DVD-RW  DVR-`** (Pioneer's universal DVR-103 boot signature), and/or a 3-byte revision field reads `"000"` → sets `DAT_005AE960` |
| 4. Flash **kernel** part | `FUN_0040A370` | 7 (offsets + save) | **0xFE** | chunks | the `0x11200`-byte kernel image (resource 131) |
| 5. Flash **normal** part | `FUN_0040A3B0` (SendChunk) | 7 (offsets + save) | **0xF0** | `0x8000` | the ~2 MB normal image (resource 132), in 32 KB chunks |
| **6. Commit / re-flash** | `FUN_0040A1D0` | **5** (download µcode **& save**) | **0xFF** | `0x100` | 256-byte control block, **timeout 180 s** ("Now internal re-flashing"); then `Sleep(5000)` |
| 7. Poll until done | `FUN_00408E60` loop | INQUIRY | — | — | waits for status bytes `02 / 04 / 12` |

**Control flow.** The table lists the distinct commands; the actual `FUN_00401DD0` order depends on a
config-table gate `DAT_005ADA48` (set from a per-update-type table `DAT_005AE1C0[]` in `FUN_00403BB0`),
which selects one of two paths. The kernel-mode switch (steps 2–3) is independent of the gate — it runs
whenever the drive isn't already in boot mode (`DAT_005AE960 == 0`).

- **Normal-only** (`DAT_005ADA48 != 0`): load normal → guard → status-wait → *switch if needed* → write
  the **full normal** image (`0xF0`) → commit. Clean and linear.
- **Combined kernel + normal** (`DAT_005ADA48 == 0`): the same, plus a sub-block that first writes *most*
  of the normal image (`0xF0`, up to size − `0x10000`), then the **kernel** image (`0xFE`), then reloads
  and writes the **full normal** image (`0xF0`) before commit.

The pre-flash helpers are: `FUN_00403D80` = model/version guard ("Model name of kernel part is not
matched" / "Need not to update"); `FUN_004041D0` + `FUN_00403AB0` = TEST-UNIT-READY / START-UNIT status
polls; `FUN_004040D0` = "does the kernel need updating?" (INQUIRY revision vs the kernel image's revision
fields at `workbuf+0x130`/`+0x150`). **One detail is not proven:** in the combined path, *why* the normal
image is written in two passes (partial `size−0x10000`, then full) — likely a drive-buffer prime/commit
requirement. The per-command CDBs above are byte-exact.

**Kernel-mode-switch CDB:**

```
3B 04 FF 00 00 00 00 01 00 00   + 256-byte DATA-OUT payload
```

It uses the WRITE-BUFFER download-microcode channel as a *command*: mode 4 ("download microcode",
temporary/not saved) to buffer id `0xFF` with a 256-byte control block (a 4-byte control field at
payload offset `0x10`, `DAT_005ADA78..7B`, computed at runtime — not a fixed constant). The **commit**
(step 6) is the same 256-byte block but mode 5 ("download microcode **and save**") with a 3-minute
timeout, which triggers the drive's internal flash write. This WRITE-BUFFER-as-command scheme matches
the known Pioneer DVR/BDR flash behaviour (cf. the open-source DVRFlash/DVRTool), which corroborates it.

Timeouts: `DAT_005AD71C` is set to 60 000 ms for the switch/chunks and 180 000 ms for the commit.

## 4. Reading the drive's firmware / kernel version

- **Firmware revision** (`1.05`): the standard SCSI **INQUIRY Product Revision Level** (bytes 32–35).
  Safe, read-only, and **already surfaced by FirmwareStudio** (`DriveIdentifier.Identify` →
  `DriveIdentity.FirmwareRevision`, shown as *Firmware* in the identity panel).
- **Kernel/bootloader version** (`ID43`): the updater reads this only **after switching the drive to
  kernel mode** (§3, now fully recovered) — an intrusive bootloader-mode reboot, which is a
  drive **state change**, not a read. There is no confirmed *normal-mode* command to read it.
  FirmwareStudio does not perform the kernel-mode switch (see *Scope*).

## Scope in FirmwareStudio

Consistent with FirmwareStudio being **strictly read-only** (no WRITE BUFFER, no flash-write, no
drive state changes):

- **Implemented (read-only):** parsing/inspecting the update file and its microcode images
  (`PioneerFirmwareImage`), and reading the drive's firmware revision via INQUIRY.
- **Not implemented:** the `WRITE BUFFER 0x3B` flash path and the kernel-mode switch. These are
  documented here as reverse-engineering reference only — the CDBs are now fully recovered (§3), but
  implementing them would break the tool's read-only guarantee and carries a real bricking risk (a
  drive-state change to the bootloader, followed by a flash write), so it stays out of scope by design.
