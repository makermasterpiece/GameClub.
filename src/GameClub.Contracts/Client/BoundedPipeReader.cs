using System.Text;

namespace GameClub.Contracts.Client;

/// <summary>Bounds allocation before accepting a newline-delimited UTF-8 IPC frame.</summary>
public static class BoundedPipeReader
{
    public static async Task<string?> ReadLineAsync(TextReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[1];
        var text = new StringBuilder();
        while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken) != 0)
        {
            if (buffer[0] == '\n')
            {
                if (text.Length > 0 && text[^1] == '\r') text.Length--;
                var line = text.ToString();
                if (Encoding.UTF8.GetByteCount(line) > ClientPipeProtocol.MaximumMessageBytes)
                    throw new IOException("IPC frame exceeded the byte limit.");
                return line;
            }

            if (text.Length >= ClientPipeProtocol.MaximumMessageBytes)
                throw new IOException("IPC frame exceeded the size limit.");
            text.Append(buffer[0]);
        }

        if (text.Length != 0) throw new IOException("IPC frame was truncated.");
        return null;
    }
}
