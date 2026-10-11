using System.IO;
using System.Text;

namespace KNE;

/// <summary>Bounds allocations even when a worker writes a line without a newline.</summary>
internal sealed class BoundedLineReader(StreamReader reader)
{
    private readonly char[] buffer = new char[4096];
    private int offset;
    private int count;
    public async Task<string?> ReadLineAsync(CancellationToken token = default)
    {
        var line = new StringBuilder();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (offset == count)
            {
                count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                offset = 0;
                if (count == 0) return line.Length == 0 ? null : line.ToString();
            }
            int newline = Array.IndexOf(buffer, '\n', offset, count - offset);
            int end = newline < 0 ? count : newline;
            if (line.Length + end - offset > 16_000_000)
                throw new InvalidDataException("Сообщение Lua превышает допустимый размер.");
            line.Append(buffer, offset, end - offset);
            offset = newline < 0 ? count : newline + 1;
            if (newline >= 0)
            {
                if (line.Length > 0 && line[^1] == '\r') line.Length--;
                return line.ToString();
            }
        }
    }
}
