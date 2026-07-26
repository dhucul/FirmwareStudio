using System.Text.Json;
using System.Text;
using FirmwareStudio.Core.Hardware;
using FirmwareStudio.Core.Logging;
using FirmwareStudio.Core.Models;

namespace FirmwareStudio.Core.Extraction;

/// <summary>Atomically writes dumped bytes, metadata, and the run-scoped command log.</summary>
public static class DumpWriter
{
    public sealed record DumpFiles(string BinPath, string SidecarPath, string? LogPath = null);

    private sealed class PendingFile(string targetPath, Action<string> write)
    {
        public string TargetPath { get; } = targetPath;
        public Action<string> Write { get; } = write;
        public string TempPath { get; set; } = "";
        public string? BackupPath { get; set; }
        public bool Promoted { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Default filename stem: <c>Vendor_Model_FwRev_yyyyMMdd-HHmmss</c> (filesystem-safe).</summary>
    public static string BuildStem(DriveIdentity id, DateTime nowUtc)
    {
        static string Clean(string? s) =>
            new(( s ?? "").Where(c => char.IsLetterOrDigit(c) || c is '-' or '.').ToArray());

        var parts = new[] { Clean(id.Vendor), Clean(id.Model), Clean(id.FirmwareRevision), nowUtc.ToString("yyyyMMdd-HHmmss") }
            .Where(s => s.Length > 0);
        string stem = string.Join("_", parts);
        return stem.Length == 0 ? $"firmware_{nowUtc:yyyyMMdd-HHmmss}" : stem;
    }

    /// <summary>Write <paramref name="binPath"/> plus <c>.json</c> and <c>.log</c> sidecars.</summary>
    public static DumpFiles Write(string binPath, ExtractionResult result, DriveIdentity id, ChipsetInfo chip,
        IReadOnlyList<CommandLogEntry> commands, DateTime timestampUtc)
    {
        byte[] firmware = result.Firmware ??
            throw new ArgumentException("A dump cannot be saved without firmware bytes.", nameof(result));
        string sidecarPath = Path.ChangeExtension(binPath, ".json");
        string logPath = Path.ChangeExtension(binPath, ".log");
        EnsureDistinctPaths(binPath, sidecarPath, logPath);
        var meta = new
        {
            tool = "FirmwareStudio",
            timestampUtc = timestampUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            drive = new
            {
                letter = id.DriveLetter.ToString(),
                vendor = id.Vendor,
                model = id.Model,
                firmwareRevision = id.FirmwareRevision,
                serial = id.Serial,
                busType = id.BusType,
            },
            chipset = new
            {
                family = chip.Family.ToString(),
                name = chip.Name,
                confidencePercent = chip.ConfidencePercent,
                evidence = chip.Evidence,
            },
            extraction = new
            {
                methodId = result.MethodId,
                methodName = result.MethodName,
                success = result.Success,
                dataLabel = result.DataLabel,
                byteCount = result.ByteCount,
                reason = result.Reason,
                summary = result.Summary,
                binFile = Path.GetFileName(binPath),
                logFile = Path.GetFileName(logPath),
            },
            commands = commands.Select(c => new
            {
                t = c.TimestampUtc.ToString("HH:mm:ss.fff"),
                cdb = c.CdbHex,
                dir = c.Direction.ToString(),
                reqLen = c.RequestedLength,
                gotLen = c.TransferredLength,
                scsiStatus = $"0x{c.ScsiStatus:X2}",
                senseKey = $"0x{c.SenseKey:X2}",
                asc = $"0x{c.Asc:X2}",
                ascq = $"0x{c.Ascq:X2}",
                status = c.StatusText,
                note = c.Note,
            }),
        };
        string json = JsonSerializer.Serialize(meta, JsonOptions);
        string commandLog = BuildCommandLog(commands, timestampUtc);
        WriteAtomically(
            new PendingFile(binPath, p => File.WriteAllBytes(p, firmware)),
            new PendingFile(sidecarPath, p => File.WriteAllText(p, json, Encoding.UTF8)),
            new PendingFile(logPath, p => File.WriteAllText(p, commandLog, Encoding.UTF8)));
        return new DumpFiles(binPath, sidecarPath, logPath);
    }

    /// <summary>Default filename stem for a hardware SPI dump: <c>SPI_&lt;maker&gt;_&lt;id&gt;_&lt;timestamp&gt;</c>.</summary>
    public static string BuildHardwareStem(SpiFlashChip chip, DateTime nowUtc)
    {
        static string Clean(string? s) =>
            new((s ?? "").Where(c => char.IsLetterOrDigit(c) || c is '-' or '.').ToArray());

        string maker = Clean(chip.Name.Split(' ').FirstOrDefault());
        string id = chip.IdHex.Replace(" ", "");
        return $"SPI_{(maker.Length > 0 ? maker + "_" : "")}{id}_{nowUtc:yyyyMMdd-HHmmss}";
    }

    /// <summary>Write a hardware SPI dump: the raw <paramref name="data"/> plus a JSON sidecar with the chip id.</summary>
    public static DumpFiles WriteHardware(string binPath, byte[] data, SpiFlashChip chip,
        IReadOnlyList<string> log, DateTime timestampUtc)
    {
        string sidecarPath = Path.ChangeExtension(binPath, ".json");
        EnsureDistinctPaths(binPath, sidecarPath);
        var meta = new
        {
            tool = "FirmwareStudio",
            source = "hardware-spi-ch341a",
            timestampUtc = timestampUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            chip = new
            {
                name = chip.Name,
                jedecId = chip.IdHex,
                manufacturerId = $"0x{chip.ManufacturerId:X2}",
                memoryType = $"0x{chip.MemoryType:X2}",
                capacityCode = $"0x{chip.CapacityCode:X2}",
                sizeBytes = chip.SizeBytes,
                voltage = chip.VoltageText,
                voltageClass = chip.Voltage.ToString(),
            },
            byteCount = data.Length,
            binFile = Path.GetFileName(binPath),
            log,
        };
        string json = JsonSerializer.Serialize(meta, JsonOptions);
        WriteAtomically(
            new PendingFile(binPath, p => File.WriteAllBytes(p, data)),
            new PendingFile(sidecarPath, p => File.WriteAllText(p, json, Encoding.UTF8)));
        return new DumpFiles(binPath, sidecarPath);
    }

    private static string BuildCommandLog(IReadOnlyList<CommandLogEntry> commands, DateTime timestampUtc)
    {
        var text = new StringBuilder();
        text.AppendLine($"# FirmwareStudio command log — {timestampUtc:yyyy-MM-dd HH:mm:ss}Z");
        foreach (var entry in commands)
            text.AppendLine($"{entry.TimestampUtc:HH:mm:ss.fff}  {entry.Text}");
        return text.ToString();
    }

    private static void EnsureDistinctPaths(params string[] paths)
    {
        if (paths.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Output paths cannot be empty.", nameof(paths));
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
            if (!unique.Add(Path.GetFullPath(path)))
                throw new ArgumentException("The .bin, .json, and .log output paths must be distinct.");
    }

    /// <summary>
    /// Stage every output beside its destination, then promote the complete set. Existing files are
    /// temporarily backed up and restored if any promotion fails, so callers never receive a mixed set.
    /// </summary>
    private static void WriteAtomically(params PendingFile[] files)
    {
        try
        {
            foreach (var file in files)
            {
                string fullTarget = Path.GetFullPath(file.TargetPath);
                string dir = Path.GetDirectoryName(fullTarget)
                    ?? throw new ArgumentException($"Output path has no directory: {file.TargetPath}");
                Directory.CreateDirectory(dir);
                file.TempPath = Path.Combine(dir,
                    $".{Path.GetFileName(fullTarget)}.{Guid.NewGuid():N}.tmp");
                file.Write(file.TempPath);
            }

            foreach (var file in files)
            {
                string fullTarget = Path.GetFullPath(file.TargetPath);
                if (File.Exists(fullTarget))
                {
                    file.BackupPath = Path.Combine(
                        Path.GetDirectoryName(fullTarget)!,
                        $".{Path.GetFileName(fullTarget)}.{Guid.NewGuid():N}.bak");
                    File.Move(fullTarget, file.BackupPath);
                }
                File.Move(file.TempPath, fullTarget);
                file.Promoted = true;
            }
        }
        catch
        {
            for (int i = files.Length - 1; i >= 0; i--)
            {
                var file = files[i];
                string fullTarget = Path.GetFullPath(file.TargetPath);
                try
                {
                    if (file.Promoted && File.Exists(fullTarget))
                        File.Delete(fullTarget);
                    if (file.BackupPath is not null && File.Exists(file.BackupPath))
                        File.Move(file.BackupPath, fullTarget, overwrite: true);
                }
                catch
                {
                    // Keep unwinding other files. The original exception remains the actionable failure.
                }
            }
            throw;
        }
        finally
        {
            foreach (var file in files)
            {
                TryDelete(file.TempPath);
                if (file.Promoted && file.BackupPath is not null)
                    TryDelete(file.BackupPath);
            }
        }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { File.Delete(path); } catch { /* best-effort cleanup */ }
    }
}
