using FirmwareStudio.Core.Models;
using FirmwareStudio.Core.Scsi;

namespace FirmwareStudio.Core.Extraction;

/// <summary>
/// Method 1 — the universal, always-safe probe. Walks READ BUFFER (0x3C) descriptor mode across buffer
/// IDs to find any readable buffer, then reads its contents (data mode) in chunks. Read-only; works on
/// any drive. What comes back is whatever the controller exposes — sometimes microcode, often nothing.
/// </summary>
public sealed class UniversalReadBufferMethod : IFirmwareExtractionMethod
{
    private const int MaxBufferId = 15;
    // 32 KiB per read. Must stay below 0x10000: the ATAPI transfer byte-count is 16-bit, so a length of
    // exactly 0x10000 truncates to 0 and the drive returns GOOD with a zero-byte transfer (empirically
    // verified — see tools/FirmwareStudio.Smoke `sweep`).
    private const int ChunkSize = 0x8000;
    private const int MaxCapacity = 16 * 1024 * 1024; // guard against absurd reported capacities

    public string Id => "universal";
    public string DisplayName => "Universal READ BUFFER probe";
    public string Description =>
        "Reads whatever the drive controller exposes via the standard READ BUFFER command. Always safe " +
        "and read-only, but many drives expose nothing useful. Output is labelled as raw controller buffer " +
        "contents, which may or may not be firmware.";

    public MethodApplicability Evaluate(DriveIdentity id, ChipsetInfo chipset)
        => MethodApplicability.Yes("Standard READ BUFFER is safe to try on any drive.");

    public ExtractionResult Extract(ScsiDevice device, DriveIdentity id, ChipsetInfo chipset,
        IProgress<ExtractionProgress> progress, CancellationToken ct)
    {
        progress.Report(new ExtractionProgress(0, "Scanning READ BUFFER descriptors"));

        var capacities = new List<(byte Id, int Capacity)>();
        for (byte bufId = 0; bufId <= MaxBufferId; bufId++)
        {
            ct.ThrowIfCancellationRequested();
            var desc = device.SendCommand(ScsiCommand.ReadBufferDescriptor(bufId), ScsiDirection.In,
                new byte[4], note: $"READ BUFFER descriptor id={bufId}");
            int cap = desc.Good && desc.Data is { Length: >= 4 } d && desc.TransferredLength >= 4
                ? ScsiCommand.ReadBe24(d, 1)
                : 0;
            if (cap > 0)
            {
                capacities.Add((bufId, cap));
                progress.Report(new ExtractionProgress(0, null, $"Buffer {bufId}: capacity {cap:N0} bytes"));
            }
        }

        if (capacities.Count == 0)
        {
            return ExtractionResult.Unsupported(Id, DisplayName,
                "This drive does not expose any readable buffer via READ BUFFER. It likely cannot be dumped " +
                "in software; a hardware SPI/flash programmer is probably required.");
        }

        foreach (var target in capacities.OrderByDescending(c => c.Capacity))
        {
            int total = Math.Min(target.Capacity, MaxCapacity);
            progress.Report(new ExtractionProgress(0, $"Reading buffer {target.Id} ({total:N0} bytes)",
                $"Trying buffer {target.Id} of {capacities.Count} advertised readable buffer(s)."));

            var outData = new byte[total];
            int got = 0;
            while (got < total)
            {
                ct.ThrowIfCancellationRequested();
                int len = Math.Min(ChunkSize, total - got);
                var read = device.SendCommand(ScsiCommand.ReadBufferData(target.Id, got, len), ScsiDirection.In,
                    new byte[len], note: $"READ BUFFER data id={target.Id} off={got} len={len}");
                if (!read.Good || read.Data is null)
                {
                    progress.Report(new ExtractionProgress(0, null,
                        $"Buffer {target.Id} stopped at offset {got:N0} ({read.StatusText})."));
                    break;
                }
                // Honour the drive-reported transfer length — a GOOD read of 0 bytes is end-of-data, not a
                // chunk of zeros to append.
                int n = read.TransferredLength;
                if (n <= 0 || n > len || n > read.Data.Length)
                {
                    progress.Report(new ExtractionProgress(0, null,
                        $"Buffer {target.Id} returned invalid/empty length {n} at offset {got:N0}."));
                    break;
                }
                Array.Copy(read.Data, 0, outData, got, n);
                got += n;
                progress.Report(new ExtractionProgress((int)(100L * got / total)));
            }

            if (got > 0)
            {
                byte[] result = got == total ? outData : outData[..got];
                return ExtractionResult.Ok(Id, DisplayName, result,
                    "controller buffer contents (may or may not be firmware/microcode)",
                    $"Read {got:N0} bytes from controller buffer {target.Id}. " +
                    $"Buffers with capacity: {string.Join(", ", capacities.Select(c => $"#{c.Id}={c.Capacity:N0}"))}.");
            }
        }

        return ExtractionResult.Unsupported(Id, DisplayName,
            $"The drive advertised {capacities.Count} buffer(s), but none returned data when read.");
    }
}
