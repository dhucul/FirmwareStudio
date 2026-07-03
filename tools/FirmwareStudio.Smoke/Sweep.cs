using FirmwareStudio.Core.Analysis;
using FirmwareStudio.Core.Drives;
using FirmwareStudio.Core.Extraction;
using FirmwareStudio.Core.Firmware;
using FirmwareStudio.Core.Logging;
using FirmwareStudio.Core.Models;
using FirmwareStudio.Core.Scsi;

namespace FirmwareStudio.Smoke;

/// <summary>
/// Empirical transfer-size sweep for the MediaTek 0xF1 cache read and the 0x3C mode-6 flash read. Instead of
/// assuming a drive "returns nothing", it asks the real hardware which requested lengths actually transfer
/// data — the definitive way to tell a genuine hardware limit from a malformed request (e.g. the 0x10000
/// 16-bit-truncation trap that made a valid cache read come back GOOD with 0 bytes). Strictly read-only.
///
/// Run elevated: <c>dotnet run --project tools/FirmwareStudio.Smoke -- sweep [D]</c>
/// </summary>
public static class Sweep
{
    // Sizes chosen to bracket the 64 KiB / 16-bit boundary: below it, right up to 0xFFFF, exactly 0x10000
    // (the truncation trap), and beyond. If transfers succeed up to some value and collapse to 0 at 0x10000,
    // that is a transport limit, not an empty drive.
    private static readonly int[] Sizes =
    {
        0x40, 0x200, 0x800, 0x1000, 0x2000, 0x4000, 0x8000,
        0xF000, 0xF800, 0xFC00, 0xFE00, 0xFFF0, 0xFFFE, 0xFFFF,
        0x10000, 0x12000, 0x18000, 0x20000, 0x40000,
    };

    public static int Run(string? driveArg)
    {
        Console.WriteLine("FirmwareStudio — transfer-size sweep (read-only)");
        Console.WriteLine("===============================================\n");

        var drives = DriveEnumerator.Scan();
        if (drives.Count == 0) { Console.WriteLine("No optical drives found."); return 1; }

        OpticalDrive? target = driveArg is { Length: >= 1 }
            ? drives.FirstOrDefault(d => char.ToUpperInvariant(d.Letter) == char.ToUpperInvariant(driveArg[0]))
            : drives[0];
        if (target is null)
        {
            Console.WriteLine($"Drive '{driveArg}' not found. Present: {string.Join(", ", drives.Select(d => d.Letter))}");
            return 1;
        }

        var logger = new FileAndMemoryLogger();
        ScsiDevice dev;
        try { dev = ScsiDevice.Open(target.Letter, logger); }
        catch (ScsiOpenException ex)
        {
            Console.WriteLine($"Open failed: {ex.Message}");
            Console.WriteLine("(Pass-through needs administrator rights — run from an elevated terminal.)");
            return 1;
        }

        using (dev)
        {
            var id = DriveIdentifier.Identify(dev, DriveEnumerator.QueryBusType(target.Letter));
            var chip = ChipsetDetector.Detect(dev, id);
            Console.WriteLine($"Drive {target.Letter}: '{id.Vendor}' '{id.Model}' fw='{id.FirmwareRevision}' " +
                              $"→ {chip.Family} ({chip.Name}, {chip.ConfidencePercent}%)\n");

            SweepOne(dev, "0xF1 MediaTek cache read", size => ScsiCommand.MediaTekReadCache(0, (uint)size), countZeroAsEmpty: true);
            SweepOne(dev, "0x3C/6 MediaTek flash read", size => ScsiCommand.ReadBufferMicrocode(0x00, 0, size), countZeroAsEmpty: false);
        }

        Console.WriteLine("\nReading:  req = bytes asked for, got = bytes the drive actually transferred.");
        Console.WriteLine("If 'got' tracks 'req' up to 0xFFFF but is 0 at exactly 0x10000, the 64 KiB request was the");
        Console.WriteLine("bug (16-bit transport truncation), not an empty drive. 'data' counts meaningful bytes.");
        return 0;
    }

    private static void SweepOne(ScsiDevice dev, string title, Func<int, byte[]> cdb, bool countZeroAsEmpty)
    {
        Console.WriteLine($"{title} — size sweep at offset 0:");
        Console.WriteLine("      req        got        status                         data-bytes  first16");
        foreach (int size in Sizes)
        {
            var buf = new byte[size];
            var r = dev.SendCommand(cdb(size), ScsiDirection.In, buf, timeoutSec: 12, note: title);
            int got = r.TransferredLength;
            int show = got < 0 || got > size ? size : got;

            long data = 0;
            for (int i = 0; i < show; i++)
            {
                byte b = buf[i];
                // "data" = bytes that carry information: for flash, non-0x00/0xFF; for cache, simply non-zero.
                if (countZeroAsEmpty ? b != 0 : (b != 0x00 && b != 0xFF)) data++;
            }
            string first16 = Convert.ToHexString(buf.AsSpan(0, Math.Min(16, Math.Max(0, show))));
            string status = r.Good ? "GOOD" : r.StatusText;
            string flag = r.Good && got == 0 ? "  ← GOOD but 0 bytes!" : "";
            Console.WriteLine($"   0x{size:X6}   0x{Math.Max(0, got):X6}   {Trunc(status, 28),-28}   {data,9}  {first16}{flag}");
        }
        Console.WriteLine();
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>
    /// Run the empirical Auto (RunAutoAsync) against a drive and report which method actually won — verifies
    /// that Auto self-selects the working method regardless of the detected chipset family.
    /// Run: <c>... -- auto [D]</c> (elevated).
    /// </summary>
    public static int AutoRun(string? driveArg)
    {
        var drives = DriveEnumerator.Scan();
        if (drives.Count == 0) { Console.WriteLine("No optical drives found."); return 1; }
        var target = driveArg is { Length: >= 1 }
            ? drives.FirstOrDefault(d => char.ToUpperInvariant(d.Letter) == char.ToUpperInvariant(driveArg[0]))
            : drives[0];
        if (target is null) { Console.WriteLine($"Drive '{driveArg}' not found."); return 1; }

        var logger = new FileAndMemoryLogger();
        ScsiDevice dev;
        try { dev = ScsiDevice.Open(target.Letter, logger); }
        catch (ScsiOpenException ex) { Console.WriteLine($"Open failed: {ex.Message}\n(Run elevated.)"); return 1; }

        using (dev)
        {
            var id = DriveIdentifier.Identify(dev, DriveEnumerator.QueryBusType(target.Letter));
            var chip = ChipsetDetector.Detect(dev, id);
            Console.WriteLine($"Drive {target.Letter}: '{id.Vendor}' '{id.Model}' → {chip.Family} ({chip.Name})\n");

            var orch = new ExtractionOrchestrator();
            var progress = new Progress<ExtractionProgress>(p => { if (p.LogLine is not null) Console.WriteLine($"  {p.LogLine}"); });
            var res = orch.RunAutoAsync(dev, id, chip, progress, System.Threading.CancellationToken.None).GetAwaiter().GetResult();

            Console.WriteLine($"\nAuto selected: {res.MethodId} — {res.MethodName}");
            Console.WriteLine($"success = {res.Success}   bytes = {res.ByteCount:N0}");
            Console.WriteLine($"summary = {res.Summary}");
        }
        return 0;
    }

    /// <summary>
    /// Run the real MediaTek 0xF1 cache extraction against a drive (read-only) and report the result — used
    /// to verify the de-mirror trim on live hardware. Run: <c>... -- cache [D]</c> (elevated).
    /// </summary>
    public static int CacheRead(string? driveArg)
    {
        var drives = DriveEnumerator.Scan();
        if (drives.Count == 0) { Console.WriteLine("No optical drives found."); return 1; }
        var target = driveArg is { Length: >= 1 }
            ? drives.FirstOrDefault(d => char.ToUpperInvariant(d.Letter) == char.ToUpperInvariant(driveArg[0]))
            : drives[0];
        if (target is null) { Console.WriteLine($"Drive '{driveArg}' not found."); return 1; }

        var logger = new FileAndMemoryLogger();
        ScsiDevice dev;
        try { dev = ScsiDevice.Open(target.Letter, logger); }
        catch (ScsiOpenException ex) { Console.WriteLine($"Open failed: {ex.Message}\n(Run elevated.)"); return 1; }

        using (dev)
        {
            var id = DriveIdentifier.Identify(dev, DriveEnumerator.QueryBusType(target.Letter));
            var chip = ChipsetDetector.Detect(dev, id);
            Console.WriteLine($"Drive {target.Letter}: '{id.Vendor}' '{id.Model}' → {chip.Family}\n");

            var method = new MediaTekCacheReadMethod();
            var progress = new Progress<ExtractionProgress>(p => { if (p.LogLine is not null) Console.WriteLine($"  {p.LogLine}"); });
            var res = method.Extract(dev, id, chip, new Progress<ExtractionProgress>(), System.Threading.CancellationToken.None);

            Console.WriteLine($"success = {res.Success}");
            Console.WriteLine($"bytes   = {res.ByteCount:N0}");
            Console.WriteLine($"label   = {res.DataLabel}");
            Console.WriteLine($"summary = {res.Summary}");
        }
        return 0;
    }

    /// <summary>
    /// Parse/characterise a PLDS/Lite-On firmware update image (`.1KN`/`.1JN`) read-only: wrapper fields
    /// (model/version/build), the entropy-classified region map, and sample strings.
    /// Run: <c>dotnet run --project tools/FirmwareStudio.Smoke -- fwfile &lt;image.1KN&gt;</c>
    /// </summary>
    public static int Firmware(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            Console.WriteLine($"Usage: fwfile <image.1KN>   (file not found: {path ?? "(none)"})");
            return 1;
        }

        byte[] data = File.ReadAllBytes(path);
        var info = FirmwareImage.Parse(data);

        Console.WriteLine($"Firmware image: {path}\n");
        Console.WriteLine($"  size      : {info.Size:N0} bytes");
        Console.WriteLine($"  magic     : {info.Magic}");
        Console.WriteLine($"  VPD image : {info.IsVpdUpdateImage}");
        Console.WriteLine($"  model     : {info.Model}");
        Console.WriteLine($"  version   : {info.Version}");
        Console.WriteLine($"  build     : {info.DateCode}");
        Console.WriteLine($"\n  {info.Describe()}\n");

        Console.WriteLine("Region map (entropy / non-zero / kind):");
        foreach (var r in info.Regions)
            Console.WriteLine($"   0x{r.Start:X6}-0x{r.End:X6}  H={r.Entropy:F2}  nz={r.NonZeroPercent,3:F0}%  {r.Kind}");

        var strs = info.Content.Strings.Where(s => s.Text.Trim().Length >= 6).Take(30).ToList();
        Console.WriteLine($"\nSample strings ({strs.Count} shown of {info.Content.Strings.Count}):");
        foreach (var (off, text) in strs)
            Console.WriteLine($"   0x{off:X6}: {text.Trim()}");
        return 0;
    }

    /// <summary>
    /// Characterise an existing dump file (read-only): what its non-zero bytes actually are — the mirror/alias
    /// period, the non-zero region map, and readable strings (firmware version/build date, media tables).
    /// Turns a "mostly zeros" image into a plain-language account of what was captured.
    /// Run: <c>dotnet run --project tools/FirmwareStudio.Smoke -- analyze &lt;dump.bin&gt;</c>
    /// </summary>
    public static int Analyze(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            Console.WriteLine($"Usage: analyze <dump.bin>   (file not found: {path ?? "(none)"})");
            return 1;
        }

        byte[] data = File.ReadAllBytes(path);
        var a = DumpAnalyzer.Analyze(data, maxStrings: 120);

        Console.WriteLine($"Dump: {path}\n");
        Console.WriteLine($"  {a.Verdict()}\n");

        Console.WriteLine("Non-zero regions (256 KiB granularity):");
        foreach (var r in a.Regions)
            Console.WriteLine($"   0x{r.Start:X7}-0x{r.End:X7}: {r.NonZero,9:N0} bytes ({r.Percent:F1}%)");

        var interesting = a.Strings.Where(s => s.Text.Length >= 5).ToList();
        Console.WriteLine($"\nReadable strings ({interesting.Count} shown):");
        foreach (var (off, text) in interesting)
            Console.WriteLine($"   0x{off:X7}: {text}");

        if (a.RepeatPeriod is int p)
            Console.WriteLine($"\nTip: the unique data is the first ~{p / 1024} KiB (0x0..0x{p:X}); the rest is a {data.Length / p}× mirror.");
        return 0;
    }
}
