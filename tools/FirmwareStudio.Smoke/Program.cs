using FirmwareStudio.Core.Drives;
using FirmwareStudio.Core.Extraction;
using FirmwareStudio.Core.Firmware;
using FirmwareStudio.Core.Logging;
using FirmwareStudio.Core.Models;
using FirmwareStudio.Core.Scsi;

// Empirical transfer-size sweep (read-only) — run elevated: `... -- sweep [D]`. Use this to prove whether a
// "GOOD but 0 bytes" read is a real hardware limit or a malformed request size.
if (args.Length >= 1 && args[0].Equals("sweep", StringComparison.OrdinalIgnoreCase))
    return FirmwareStudio.Smoke.Sweep.Run(args.Length >= 2 ? args[1] : null);

// Characterise an existing dump: `... -- analyze <dump.bin>`. Shows what a mostly-zero image really contains.
if (args.Length >= 1 && args[0].Equals("analyze", StringComparison.OrdinalIgnoreCase))
    return FirmwareStudio.Smoke.Sweep.Analyze(args.Length >= 2 ? args[1] : null);

// Parse a PLDS/Lite-On firmware update image: `... -- fwfile <image.1KN>`.
if (args.Length >= 1 && args[0].Equals("fwfile", StringComparison.OrdinalIgnoreCase))
    return FirmwareStudio.Smoke.Sweep.Firmware(args.Length >= 2 ? args[1] : null);

// Run the real 0xF1 cache extraction (verifies the de-mirror trim on live hardware): `... -- cache [D]`.
if (args.Length >= 1 && args[0].Equals("cache", StringComparison.OrdinalIgnoreCase))
    return FirmwareStudio.Smoke.Sweep.CacheRead(args.Length >= 2 ? args[1] : null);

// Run empirical Auto on a drive and report which method actually won: `... -- auto [D]`.
if (args.Length >= 1 && args[0].Equals("auto", StringComparison.OrdinalIgnoreCase))
    return FirmwareStudio.Smoke.Sweep.AutoRun(args.Length >= 2 ? args[1] : null);

// Full extraction-method diagnostic — try every method and report results: `... -- diagnose [D]`.
if (args.Length >= 1 && args[0].Equals("diagnose", StringComparison.OrdinalIgnoreCase))
    return FirmwareStudio.Smoke.Sweep.Diagnose(args.Length >= 2 ? args[1] : null);

Console.WriteLine("FirmwareStudio smoke test");
Console.WriteLine("=========================\n");

// 1. Struct-layout ABI check — nothing else can be trusted if this is wrong.
string? layout = ScsiDevice.VerifyLayout();
Console.WriteLine(layout is null
    ? "[OK]   SPTD layout: 56-byte struct, sense at offset 56"
    : $"[FAIL] {layout}");
if (layout is not null) return 2;

// 1b. PLDS/Lite-On vendor command 0xDF CDB shape (offline) — must match the Plextor PX-891SAF updater's
//     drive-state read (mode 0, bufferId 0x0F, arg3 0x20): DF 00 0F 20 00 ... (12-byte CDB).
{
    byte[] cdb = ScsiCommand.PldsVendor(0x00, 0x0F, 0x20, 0x00);
    bool tailZero = true;
    for (int i = 5; i < cdb.Length; i++) if (cdb[i] != 0) tailZero = false;
    bool shapeOk = cdb.Length == 12 && cdb[0] == 0xDF && cdb[1] == 0x00 && cdb[2] == 0x0F
                   && cdb[3] == 0x20 && cdb[4] == 0x00 && tailZero;
    Console.WriteLine(shapeOk
        ? "[OK]   PLDS 0xDF CDB builder: DF 00 0F 20 00 ... (12 bytes)"
        : $"[FAIL] PLDS 0xDF CDB wrong: {Convert.ToHexString(cdb)}");
    bool registered = new ExtractionOrchestrator().ById("plds") is not null;
    Console.WriteLine(registered
        ? "[OK]   'plds' extraction method registered in the orchestrator."
        : "[FAIL] 'plds' extraction method not registered.");
    if (!shapeOk || !registered) return 2;
}

// 1c. MediaTek/Lite-On flash-read CDB shape (offline): READ BUFFER (0x3C) in mode 6
//     (DOWNLOAD_MICROCODE_WITH_OFFSETS) with a 24-bit flash offset — the Lite-On/MediaTek firmware-flash
//     read (redumper drive/flash_mt1959.ixx). Sample: read 0x8000 bytes at flash offset 0x3000.
{
    byte[] cdb = ScsiCommand.ReadBufferMicrocode(0x00, 0x3000, 0x8000);
    bool shapeOk = cdb.Length == 10 && cdb[0] == 0x3C && cdb[1] == 0x06 && cdb[2] == 0x00
                   && cdb[3] == 0x00 && cdb[4] == 0x30 && cdb[5] == 0x00   // offset 0x003000 (BE24)
                   && cdb[6] == 0x00 && cdb[7] == 0x80 && cdb[8] == 0x00;  // length 0x008000 (BE24)
    Console.WriteLine(shapeOk
        ? "[OK]   MTK flash-read CDB builder: 3C 06 00 00 30 00 00 80 00 (READ BUFFER mode 6)"
        : $"[FAIL] MTK flash-read CDB wrong: {Convert.ToHexString(cdb)}");
    bool registered = new ExtractionOrchestrator().ById("mtk-flash") is not null;
    Console.WriteLine(registered
        ? "[OK]   'mtk-flash' extraction method registered in the orchestrator."
        : "[FAIL] 'mtk-flash' extraction method not registered.");
    if (!shapeOk || !registered) return 2;
}

// 1d. Flash-read applicability logic (offline): the newer MT62xx generation (Plextor PX-8xx) rejects mode 6
//     so it must be "maybe"; a generic MediaTek iHAS must be "applicable"; a non-MediaTek drive "n/a".
{
    var flash = new MtkFlashReadMethod();
    var mt62 = flash.Evaluate(
        new DriveIdentity('D', "PLEXTOR", "PX-891SAF PLUS", "1.KN", null, "SATA"),
        new ChipsetInfo(ChipsetFamily.MediaTek, "MediaTek MT62xx (Plextor/PLDS PX-8xx)", 92, new[] { "t" }));
    var ihas = flash.Evaluate(
        new DriveIdentity('E', "LITE-ON", "iHAS124   F", "FL0N", null, "SATA"),
        new ChipsetInfo(ChipsetFamily.MediaTek, "MediaTek (LiteOn iHAS)", 90, new[] { "t" }));
    var nonMtk = flash.Evaluate(
        new DriveIdentity('F', "PIONEER", "BD-RW BDR-209", "1.00", null, "SATA"),
        new ChipsetInfo(ChipsetFamily.Pioneer, "Pioneer Custom", 70, new[] { "t" }));
    bool logicOk = mt62.Level == Applicability.Maybe
                   && ihas.Level == Applicability.Applicable
                   && nonMtk.Level == Applicability.NotApplicable;
    Console.WriteLine(logicOk
        ? "[OK]   mtk-flash applicability: MT62xx→maybe, iHAS→applicable, non-MediaTek→n/a"
        : $"[FAIL] mtk-flash applicability wrong: MT62xx={mt62.Level}, iHAS={ihas.Level}, non-MTK={nonMtk.Level}");
    if (!logicOk) return 2;
}

// 1e. NEC/Renesas ReadRAM (0xCC) / ReadBoot (0xCD) CDB shapes + table (offline, binflash-ported).
//     Read-only: 0xCC byte 1 carries ONLY the bank bit, never the 0x01/0x04/0x06 erase/safe-mode sub-modes.
{
    byte[] ram = ScsiCommand.NecReadRam(false, 0x30000, 0x1000, 0);
    bool ramOk = ram.Length == 12 && ram[0] == 0xCC && ram[1] == 0x00
                 && ram[2] == 0x00 && ram[3] == 0x03 && ram[4] == 0x00 && ram[5] == 0x00   // addr 0x00030000 (BE32)
                 && ram[7] == 0x10 && ram[8] == 0x00;                                       // length 0x1000 (BE16)
    byte[] ramBankId = ScsiCommand.NecReadRam(true, 0x30000, 0x1000, 0x42);
    bool bankIdOk = ramBankId[1] == 0x10 && ramBankId[9] == 0x52 && ramBankId[10] == 0x44 && ramBankId[11] == 0x42;
    byte[] boot = ScsiCommand.NecReadBoot(false);
    bool bootOk = boot.Length == 12 && boot[0] == 0xCD && boot[1] == 0x00
                  && boot[2] == 0x52 && boot[3] == 0x44 && boot[4] == 0x42
                  && boot[5] == 0x4F && boot[6] == 0x4F && boot[7] == 0x54;                 // "RDBOOT"
    Console.WriteLine(ramOk && bankIdOk
        ? "[OK]   NEC ReadRAM CDB builder: CC 00 00 03 00 00 00 10 00 (0xCC, bank bit only; +'RD'+id)"
        : $"[FAIL] NEC ReadRAM CDB wrong: {Convert.ToHexString(ram)} / {Convert.ToHexString(ramBankId)}");
    Console.WriteLine(bootOk
        ? "[OK]   NEC ReadBoot CDB builder: CD 00 52 44 42 4F 4F 54 (RDBOOT)"
        : $"[FAIL] NEC ReadBoot CDB wrong: {Convert.ToHexString(boot)}");

    var g245 = NecDriveTable.Signatures.FirstOrDefault(s => s.ProbeAddress == 0x10800 && s.Code == "G245");
    bool tableOk = NecDriveTable.Signatures.Length > 300
                   && g245.Code == "G245" && g245.Flash == NecFlashType.Size2MOpti3
                   && NecDriveTable.RegionsFor(NecFlashType.Size2MOpti3).Length == 1
                   && NecDriveTable.RegionsFor(NecFlashType.Size2MOptiBD).Length == 0;   // BD dump unsupported
    bool registered = new ExtractionOrchestrator().ById("nec") is not null;
    Console.WriteLine(tableOk
        ? $"[OK]   NEC drive table: {NecDriveTable.Signatures.Length} signatures; G245→iHAS324Y (Size2MOpti3)"
        : "[FAIL] NEC drive table wrong");
    Console.WriteLine(registered
        ? "[OK]   'nec' extraction method registered in the orchestrator."
        : "[FAIL] 'nec' extraction method not registered.");
    if (!ramOk || !bankIdOk || !bootOk || !tableOk || !registered) return 2;
}

// 1f. FirmwareImage parser (offline): a synthetic PLDS VPD update image must be recognised and its wrapper
//     fields (model @0x414, version @end-4 — the exact offsets the 891SAF updater reads) extracted; the
//     high-entropy body must classify as encrypted; a low-entropy buffer must NOT be flagged as a VPD image.
{
    var img = new byte[0x21000];
    Array.Copy("VPD_update_file"u8.ToArray(), 0, img, 0x400, 15);
    Array.Copy("PLEXTOR PX-891SAF PLUS  "u8.ToArray(), 0, img, 0x414, 24);
    Array.Copy("1.KN"u8.ToArray(), 0, img, img.Length - 4, 4);
    uint s = 0x12345678u;                                  // deterministic high-entropy body at 0x10000+
    for (int i = 0x10000; i < 0x20000; i++) { s = s * 1664525u + 1013904223u; img[i] = (byte)(s >> 24); }
    var info = FirmwareImage.Parse(img);
    bool bodyEnc = info.Regions.Any(r => r.Kind.StartsWith("encrypted", StringComparison.Ordinal));
    var plain = FirmwareImage.Parse(new byte[0x11000]);   // all-zero: not a VPD image
    bool ok = info.IsVpdUpdateImage && info.Model == "PLEXTOR PX-891SAF PLUS" && info.Version == "1.KN"
              && bodyEnc && !plain.IsVpdUpdateImage;
    Console.WriteLine(ok
        ? "[OK]   FirmwareImage parser: VPD image recognised, model/version extracted, body classified encrypted, non-VPD rejected"
        : $"[FAIL] FirmwareImage parser wrong: vpd={info.IsVpdUpdateImage} model='{info.Model}' ver='{info.Version}' bodyEnc={bodyEnc} plainVpd={plain.IsVpdUpdateImage}");
    if (!ok) return 2;
}

// 2. Enumerate optical drives (no handle, no elevation needed).
var drives = DriveEnumerator.Scan();
Console.WriteLine($"[OK]   Optical drives found: {drives.Count}");
foreach (var d in drives) Console.WriteLine($"         {d.Display}");
if (drives.Count == 0)
{
    Console.WriteLine("\nNo optical drives present — enumeration works, nothing to probe.");
    return 0;
}

// Hardware SPI (CH341) path — expected to report "no adapter/DLL" cleanly in this environment.
Console.WriteLine("\nHardware SPI (CH341) check:");
try
{
    using var hw = FirmwareStudio.Core.Hardware.Ch341Device.Open();
    Console.WriteLine($"[OK]   CH341 adapter connected (DLL v{hw.DllVersion})");
    var chip = FirmwareStudio.Core.Hardware.SpiNorFlash.ReadId(hw, s => Console.WriteLine($"         {s}"));
    Console.WriteLine($"[OK]   chip: {chip.Name}  size={chip.SizeText}");
}
catch (FirmwareStudio.Core.Hardware.Ch341Exception ex)
{
    Console.WriteLine($"[OK]   graceful: {ex.Message}");
}
catch (Exception ex)
{
    Console.WriteLine($"[FAIL] unexpected {ex.GetType().Name}: {ex.Message}");
}

// Voltage heuristic check against known JEDEC IDs.
{
    (byte man, byte type, FirmwareStudio.Core.Hardware.FlashVoltage want, string label)[] cases =
    {
        (0xEF, 0x40, FirmwareStudio.Core.Hardware.FlashVoltage.ThreeVolt,    "Winbond W25Q..V 3.3V"),
        (0xEF, 0x60, FirmwareStudio.Core.Hardware.FlashVoltage.OneEightVolt, "Winbond W25Q..W 1.8V"),
        (0xC2, 0x20, FirmwareStudio.Core.Hardware.FlashVoltage.ThreeVolt,    "Macronix MX25L 3.3V"),
        (0xC2, 0x25, FirmwareStudio.Core.Hardware.FlashVoltage.OneEightVolt, "Macronix MX25U 1.8V"),
        (0x20, 0xBB, FirmwareStudio.Core.Hardware.FlashVoltage.OneEightVolt, "Micron MT25Q 1.8V"),
        (0xAA, 0xBB, FirmwareStudio.Core.Hardware.FlashVoltage.Unknown,      "unknown maker"),
    };
    bool allOk = true;
    foreach (var c in cases)
    {
        var got = FirmwareStudio.Core.Hardware.SpiNorFlash.GuessVoltage(c.man, c.type);
        bool ok = got == c.want;
        allOk &= ok;
        Console.WriteLine($"   {(ok ? "ok  " : "FAIL")} {c.man:X2} {c.type:X2} -> {got}  ({c.label})");
    }
    Console.WriteLine(allOk ? "[OK]   voltage heuristic correct for known IDs" : "[FAIL] voltage heuristic wrong");
}

var target = drives[0];
Console.WriteLine($"\nProbing {target.Display}\n");

var logger = new FileAndMemoryLogger();
logger.CommandLogged += e => Console.WriteLine($"   scsi | {e.Text}");
logger.InfoLogged += m => Console.WriteLine($"   info | {m}");

ScsiDevice dev;
try
{
    dev = ScsiDevice.Open(target.Letter, logger);
}
catch (ScsiOpenException ex)
{
    Console.WriteLine($"[WARN] {ex.Message}");
    Console.WriteLine("\n(INQUIRY / READ BUFFER need administrator rights; enumeration above still passed.)");
    return 0;
}

using (dev)
{
    // 3. Identify.
    string bus = DriveEnumerator.QueryBusType(target.Letter);
    var id = DriveIdentifier.Identify(dev, bus);
    Console.WriteLine($"\n[OK]   Identity: vendor='{id.Vendor}' model='{id.Model}' " +
                      $"fw='{id.FirmwareRevision}' serial='{id.Serial ?? "(n/a)"}' bus={id.BusType}");

    // 4. Chipset detection.
    var chip = ChipsetDetector.Detect(dev, id);
    Console.WriteLine($"[OK]   Chipset: {chip.Family} — {chip.Name} ({chip.ConfidencePercent}%)");
    foreach (var e in chip.Evidence) Console.WriteLine($"         - {e}");

    // 5. Safe READ BUFFER descriptor walk + first non-empty buffer hexdump.
    Console.WriteLine("\nREAD BUFFER descriptor walk:");
    byte? firstNonEmpty = null;
    int firstCap = 0;
    for (byte b = 0; b <= 7; b++)
    {
        var desc = dev.SendCommand(ScsiCommand.ReadBufferDescriptor(b), ScsiDirection.In, new byte[4],
            note: $"READ BUFFER descriptor id={b}");
        int cap = desc.Good && desc.Data is { Length: >= 4 } dd ? ScsiCommand.ReadBe24(dd, 1) : 0;
        Console.WriteLine($"   buffer {b}: {(desc.Good ? $"capacity {cap:N0} bytes" : desc.StatusText)}");
        if (firstNonEmpty is null && cap > 0) { firstNonEmpty = b; firstCap = cap; }
    }

    if (firstNonEmpty is byte bid)
    {
        int len = Math.Min(64, firstCap);
        var read = dev.SendCommand(ScsiCommand.ReadBufferData(bid, 0, len), ScsiDirection.In, new byte[len],
            note: $"READ BUFFER data id={bid} off=0 len={len}");
        if (read.Good && read.Data is not null)
        {
            Console.WriteLine($"\nFirst {len} bytes of buffer {bid}:");
            Console.WriteLine("   " + Convert.ToHexString(read.Data));
        }
    }
    else
    {
        Console.WriteLine("\nNo readable controller buffer — this drive likely needs a hardware programmer.");
    }

    // 6. If MediaTek, sample the cache (0xF1) to confirm the money path returns real, non-zero data.
    if (chip.Family == FirmwareStudio.Core.Models.ChipsetFamily.MediaTek)
    {
        Console.WriteLine("\nMediaTek cache sample (0xF1), 256 KiB in 16 KiB reads:");
        long nonZero = 0, totalBytes = 0;
        for (uint off = 0; off < 0x40000; off += 0x4000)   // 16 KiB chunks — a 0x10000 request truncates to 0 bytes
        {
            var r = dev.SendCommand(ScsiCommand.MediaTekReadCache(off, 0x4000), ScsiDirection.In,
                new byte[0x4000], note: $"MediaTek 0xF1 off={off}");
            if (!r.Good || r.Data is null) { Console.WriteLine($"   off {off}: {r.StatusText}"); break; }
            int got = r.TransferredLength; if (got < 0 || got > 0x4000) got = 0x4000;
            if (got == 0) { Console.WriteLine($"   off {off}: GOOD but 0 bytes"); break; }
            totalBytes += got;
            for (int i = 0; i < got; i++) if (r.Data[i] != 0) nonZero++;
        }
        double pct = totalBytes == 0 ? 0 : 100.0 * nonZero / totalBytes;
        Console.WriteLine($"   read {totalBytes:N0} bytes, {pct:F1}% non-zero " +
                          (pct > 1 ? "→ cache holds real data (firmware image path works)." : "→ mostly zero; cache may be empty without media."));

        // 6b. MediaTek/Lite-On flash read (READ BUFFER 0x3C mode 6) — the firmware-flash path. Read-only.
        //     Probe the flash at offset 0 and at the config region (0x3000) redumper reads to fingerprint
        //     the drive. Reports whether the drive accepts mode 6 and whether the bytes look firmware-like.
        Console.WriteLine("\nMediaTek flash read probe (READ BUFFER 0x3C mode 6):");
        foreach (int off in new[] { 0x0000, 0x3000 })
        {
            var r = dev.SendCommand(ScsiCommand.ReadBufferMicrocode(0x00, off, 256), ScsiDirection.In,
                new byte[256], note: $"MTK flash read (0x3C/6) off=0x{off:X} len=0x100");
            if (!r.Good || r.Data is null)
            {
                Console.WriteLine($"   off 0x{off:X4}: {r.StatusText}"
                    + (r.SenseInfo.Key == 0x05 ? "  → drive does not implement mode 6 (not a MTK flash-download drive)" : ""));
                continue;
            }
            int meaningful = 0;
            foreach (var b in r.Data) if (b != 0x00 && b != 0xFF) meaningful++;
            Console.WriteLine($"   off 0x{off:X4}: {r.Data.Length} bytes, {100.0 * meaningful / r.Data.Length:F0}% firmware-like (non-0x00/0xFF)"
                + (meaningful > 0 ? "  → flash read WORKS on this drive." : "  → empty/erased here."));
            Console.WriteLine("      " + Convert.ToHexString(r.Data.AsSpan(0, Math.Min(32, r.Data.Length))));
        }
    }

    // 7. Orchestrator wrapper + DumpWriter file round-trip.
    Console.WriteLine("\nOrchestrator + DumpWriter check:");
    var orch = new ExtractionOrchestrator();
    var progress = new Progress<ExtractionProgress>(p => { if (p.LogLine is not null) Console.WriteLine($"      {p.LogLine}"); });
    var universal = orch.ById("universal")!;
    var res = await orch.RunAsync(dev, id, chip, universal, progress, System.Threading.CancellationToken.None);
    Console.WriteLine($"   universal → success={res.Success}: {res.Summary}");

    // Exercise DumpWriter with a synthetic success (drive may legitimately expose nothing).
    var pattern = new byte[4096];
    for (int i = 0; i < pattern.Length; i++) pattern[i] = (byte)(i & 0xFF);
    var synthetic = ExtractionResult.Ok("selftest", "DumpWriter self-test", pattern, "test pattern", "synthetic 4 KiB dump");
    string dir = Path.Combine(Path.GetTempPath(), "fwstudio-smoke");
    string binPath = Path.Combine(dir, DumpWriter.BuildStem(id, DateTime.UtcNow) + ".selftest.bin");
    var files = DumpWriter.Write(binPath, synthetic, id, chip, logger.Entries, DateTime.UtcNow);
    long binLen = new FileInfo(files.BinPath).Length;
    long jsonLen = new FileInfo(files.SidecarPath).Length;
    Console.WriteLine($"   wrote .bin  ({binLen:N0} bytes) → {files.BinPath}");
    Console.WriteLine($"   wrote .json ({jsonLen:N0} bytes) → {files.SidecarPath}");
    bool ok = binLen == 4096 && jsonLen > 0;
    Console.WriteLine(ok ? "[OK]   DumpWriter produced .bin + .json sidecar." : "[FAIL] DumpWriter output wrong.");
    try { Directory.Delete(dir, recursive: true); } catch { /* leave temp files if in use */ }
}

// Per-drive vendor-command fingerprint across ALL drives (read-only) — the definitive diagnostic: chipset
// family plus the exact sense for each software firmware-read path. Run with the iHAS/Optiarc attached.
Console.WriteLine("\nVendor-command fingerprint across all drives (read-only):");
foreach (var d in drives)
{
    try
    {
        using var ndev = ScsiDevice.Open(d.Letter, new FileAndMemoryLogger());
        var did = DriveIdentifier.Identify(ndev, DriveEnumerator.QueryBusType(d.Letter));
        var chip = ChipsetDetector.Detect(ndev, did);
        Console.WriteLine($"   {d.Display}");
        Console.WriteLine($"      identity : vendor='{did.Vendor}' model='{did.Model}' fw='{did.FirmwareRevision}'");
        Console.WriteLine($"      chipset  : {chip.Family} — {chip.Name} ({chip.ConfidencePercent}%)");

        var f1 = ndev.SendCommand(ScsiCommand.MediaTekReadCache(0, 64), ScsiDirection.In, new byte[64], note: "0xF1");
        var m6 = ndev.SendCommand(ScsiCommand.ReadBufferMicrocode(0x00, 0x3000, 0x100), ScsiDirection.In, new byte[0x100], note: "0x3C/6");
        var cc = ndev.SendCommand(ScsiCommand.NecReadRam(false, 0x4500, 0x20), ScsiDirection.In, new byte[0x20], note: "0xCC");
        Console.WriteLine($"      0xF1 cache (MediaTek)   : {(f1.Good ? "GOOD" : f1.StatusText)}");
        Console.WriteLine($"      0x3C/6 flash (MTK mode6): {(m6.Good ? "GOOD" : m6.StatusText)}");
        Console.WriteLine($"      0xCC ReadRAM (NEC)      : {(cc.Good ? "GOOD" : cc.StatusText)}");

        // If 0xF1 is accepted, sample up to 1 MiB of the controller cache/RAM to see whether it actually
        // holds firmware-like data (non-0x00/0xFF) or reads back empty. This is the last software hope for
        // a MediaTek drive that rejects mode 6.
        if (f1.Good)
        {
            long nz = 0, tot = 0;
            for (uint off = 0; off < 0x40000; off += 0x4000)   // 256 KiB sample in 16 KiB reads (0x10000 truncates to 0)
            {
                var s = ndev.SendCommand(ScsiCommand.MediaTekReadCache(off, 0x4000), ScsiDirection.In,
                    new byte[0x4000], timeoutSec: 8, note: $"0xF1 off=0x{off:X}");
                if (!s.Good || s.Data is null) break;
                int got = s.TransferredLength; if (got < 0 || got > 0x4000) got = 0x4000;
                if (got == 0) break;
                tot += got;
                for (int i = 0; i < got; i++) { byte b = s.Data[i]; if (b != 0x00 && b != 0xFF) nz++; }
            }
            Console.WriteLine($"      0xF1 sample: {tot:N0} B, {(tot == 0 ? 0 : 100.0 * nz / tot):F1}% firmware-like"
                + (nz > 0 ? "  → cache holds real data!" : "  → empty/erased."));
        }

        // ATA pass-through channel test (read-only): IDENTIFY PACKET DEVICE (0xA1). Does a RAW, non-packet
        // ATA command reach this ATAPI drive on this AHCI controller? This is the prerequisite for any
        // MediaTek "vendor mode" (PortIO) route — if the driver refuses raw ATA here, that route is dead.
        {
            byte[] tf = { 0x00, 0x01, 0x00, 0x00, 0x00, 0xA0, 0xA1, 0x00 };   // IDENTIFY PACKET DEVICE
            var ata = ndev.SendAta(tf, dataIn: true, new byte[512], note: "IDENTIFY PACKET DEVICE");
            if (!ata.IoOk)
            {
                Console.WriteLine($"      ATA pass-through (0xA1)  : REFUSED (win32={ata.Win32Error}) → raw ATA not available for this drive.");
            }
            else
            {
                string model = "";
                if (ata.Data is { Length: >= 94 } dd)
                {
                    var sb = new System.Text.StringBuilder();
                    for (int i = 54; i < 94; i += 2) { sb.Append((char)dd[i + 1]); sb.Append((char)dd[i]); }
                    model = sb.ToString().Trim();
                }
                // A real IDENTIFY response has DRDY (status bit 6) set or returns a model string; an IOCTL that
                // "succeeds" with status 0x00 and an empty model (e.g. a virtual drive) did not really execute.
                bool ready = (ata.Status & 0x40) != 0 || model.Length > 0;
                Console.WriteLine($"      ATA pass-through (0xA1)  : {(ready ? "OK" : "accepted-but-idle")} status=0x{ata.Status:X2} model='{model}'"
                    + (ready ? " → raw ATA WORKS (channel open)." : " → IOCTL accepted but no real ATA response."));
            }
        }

        var ident = NecDriveTable.Identify(ndev);
        if (ident.AnyAccepted)
        {
            string codes = string.Join(", ", ident.Reads.Select(r =>
                $"0x{r.Addr:X}='{new string(r.Code.Select(c => c >= 32 && c < 127 ? c : '.').ToArray())}'"));
            Console.WriteLine(ident.Match is NecDriveTable.Signature s
                ? $"      → NEC identify: {s.Model} ({s.Flash}) [{codes}] — NEC read APPLIES."
                : $"      → NEC identify: 0xCC works, unrecognised [{codes}] (send me these to extend the table).");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   {d.Display}: {ex.Message}");
    }
}

Console.WriteLine("\nSmoke test complete.");
return 0;
