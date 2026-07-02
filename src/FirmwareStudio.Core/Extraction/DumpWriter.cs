using System.Text.Json;
using FirmwareStudio.Core.Hardware;
using FirmwareStudio.Core.Logging;
using FirmwareStudio.Core.Models;

namespace FirmwareStudio.Core.Extraction;

/// <summary>Writes the dumped bytes (.bin) and a JSON metadata sidecar next to it.</summary>
public static class DumpWriter
{
    public sealed record DumpFiles(string BinPath, string SidecarPath);

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

    /// <summary>Write <paramref name="binPath"/> plus a <c>.json</c> sidecar beside it. Returns both paths.</summary>
    public static DumpFiles Write(string binPath, ExtractionResult result, DriveIdentity id, ChipsetInfo chip,
        IReadOnlyList<CommandLogEntry> commands, DateTime timestampUtc)
    {
        string? dir = Path.GetDirectoryName(binPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        File.WriteAllBytes(binPath, result.Firmware ?? Array.Empty<byte>());

        string sidecarPath = Path.ChangeExtension(binPath, ".json");
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
        File.WriteAllText(sidecarPath, JsonSerializer.Serialize(meta, JsonOptions));
        return new DumpFiles(binPath, sidecarPath);
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
        string? dir = Path.GetDirectoryName(binPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        File.WriteAllBytes(binPath, data);

        string sidecarPath = Path.ChangeExtension(binPath, ".json");
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
        File.WriteAllText(sidecarPath, JsonSerializer.Serialize(meta, JsonOptions));
        return new DumpFiles(binPath, sidecarPath);
    }
}
