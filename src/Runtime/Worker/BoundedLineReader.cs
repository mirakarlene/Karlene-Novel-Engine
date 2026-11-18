using System.IO;
using System.Text;

namespace KNE;

/// <summary>Reads a fresh worker stream in bounded chunks without waiting beyond a complete line.</summary>
internal sealed class BoundedLineReader(StreamReader reader)
{
    // StreamReader.ReadAsync(char[4096]) can wait to fill the requested characters when a
    // pipe ends exactly at its internal byte-buffer boundary, even after receiving '\n'.
    // Read the fresh byte stream instead; the decoder retains split UTF-8 sequences.
    private readonly byte[] bytes = new byte[4096];
    private readonly char[] buffer = new char[reader.CurrentEncoding.GetMaxCharCount(4096)];
    private readonly Decoder decoder = reader.CurrentEncoding.GetDecoder();
    private bool atStart = true;
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
                int received = await reader.BaseStream.ReadAsync(bytes.AsMemory(), token).ConfigureAwait(false);
                count = decoder.GetChars(bytes, 0, received, buffer, 0, flush: received == 0);
                offset = 0;
                if (count == 0)
                {
                    if (received == 0) return line.Length == 0 ? null : line.ToString();
                    continue;
                }
                if (atStart) { atStart = false; if (buffer[0] == '\uFEFF') offset = 1; }
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
