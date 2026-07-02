using System.Text;

namespace FirmwareStudio.Core.Logging;

/// <summary>
/// Keeps every logged command in memory (for the sidecar/UI) and, once <see cref="StartFile"/> is called,
/// appends each line to a <c>.log</c> file. Raises <see cref="CommandLogged"/> / <see cref="InfoLogged"/>
/// so the WPF layer can marshal lines onto the UI thread. Thread-safe.
/// </summary>
public sealed class FileAndMemoryLogger : IScsiLogger, IDisposable
{
    private readonly object _gate = new();
    private readonly List<CommandLogEntry> _entries = new();
    private StreamWriter? _writer;

    public event Action<CommandLogEntry>? CommandLogged;
    public event Action<string>? InfoLogged;

    public IReadOnlyList<CommandLogEntry> Entries
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    /// <summary>Begin appending the log to a file. Any prior file is closed first.</summary>
    public void StartFile(string path)
    {
        lock (_gate)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
            _writer.WriteLine($"# FirmwareStudio command log — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        }
    }

    public void OnCommand(CommandLogEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            _writer?.WriteLine($"{entry.TimestampUtc:HH:mm:ss.fff}  {entry.Text}");
        }
        CommandLogged?.Invoke(entry);
    }

    public void Info(string message)
    {
        lock (_gate)
            _writer?.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff}  # {message}");
        InfoLogged?.Invoke(message);
    }

    /// <summary>Write every in-memory entry to <paramref name="path"/> as a standalone log file.</summary>
    public void WriteAllTo(string path)
    {
        lock (_gate)
        {
            using var w = new StreamWriter(path, append: false, Encoding.UTF8);
            w.WriteLine($"# FirmwareStudio command log — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
            foreach (var e in _entries)
                w.WriteLine($"{e.TimestampUtc:HH:mm:ss.fff}  {e.Text}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }
}
