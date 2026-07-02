using System.Text;

namespace FirmwareStudio.Wpf.Services;

/// <summary>Formats a byte[] as a classic offset / hex / ASCII dump for the preview pane.</summary>
public static class HexDump
{
    public static string Format(byte[] data, int maxBytes = 8192)
    {
        if (data.Length == 0) return "(empty)";
        int n = Math.Min(data.Length, maxBytes);
        var sb = new StringBuilder(n * 4);
        for (int i = 0; i < n; i += 16)
        {
            sb.Append(i.ToString("X8")).Append("  ");
            int row = Math.Min(16, n - i);
            for (int j = 0; j < 16; j++)
            {
                if (j < row) sb.Append(data[i + j].ToString("X2")).Append(' ');
                else sb.Append("   ");
                if (j == 7) sb.Append(' ');
            }
            sb.Append(' ');
            for (int j = 0; j < row; j++)
            {
                byte b = data[i + j];
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }
            sb.Append('\n');
        }
        if (data.Length > n)
            sb.Append($"\n… {data.Length - n:N0} more bytes not shown (preview capped at {maxBytes:N0}).");
        return sb.ToString();
    }
}
