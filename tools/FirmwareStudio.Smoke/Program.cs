using FirmwareStudio.Core.Drives;
using FirmwareStudio.Core.Extraction;
using FirmwareStudio.Core.Logging;
using FirmwareStudio.Core.Models;
using FirmwareStudio.Core.Scsi;

Console.WriteLine("FirmwareStudio smoke test");
Console.WriteLine("=========================\n");

// 1. Struct-layout ABI check — nothing else can be trusted if this is wrong.
string? layout = ScsiDevice.VerifyLayout();
Console.WriteLine(layout is null
    ? "[OK]   SPTD layout: 56-byte struct, sense at offset 56"
    : $"[FAIL] {layout}");
if (layout is not null) return 2;

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
        Console.WriteLine("\nMediaTek cache sample (0xF1), 1 x 64 KiB:");
        long nonZero = 0, totalBytes = 0;
        for (uint off = 0; off < 1 * 64 * 1024; off += 64 * 1024)
        {
            var r = dev.SendCommand(ScsiCommand.MediaTekReadCache(off, 64 * 1024), ScsiDirection.In,
                new byte[64 * 1024], note: $"MediaTek 0xF1 off={off}");
            if (!r.Good || r.Data is null) { Console.WriteLine($"   off {off}: {r.StatusText}"); break; }
            totalBytes += r.Data.Length;
            foreach (var b in r.Data) if (b != 0) nonZero++;
        }
        double pct = totalBytes == 0 ? 0 : 100.0 * nonZero / totalBytes;
        Console.WriteLine($"   read {totalBytes:N0} bytes, {pct:F1}% non-zero " +
                          (pct > 1 ? "→ cache holds real data (firmware image path works)." : "→ mostly zero; cache may be empty without media."));
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

Console.WriteLine("\nSmoke test complete.");
return 0;
