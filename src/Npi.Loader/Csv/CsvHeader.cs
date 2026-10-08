using System.Text;

namespace Npi.Loader.Csv;

/// <summary>The header row of an NPPES CSV and the line terminator the file uses.</summary>
public sealed record CsvHeader(IReadOnlyList<string> Columns, string LineTerminator)
{
    private const int MaxHeaderBytes = 1 << 20;

    /// <summary>Reads the first line of <paramref name="stream"/> (consuming only that line's bytes and a little more).</summary>
    public static CsvHeader Read(Stream stream)
    {
        var bytes = new List<byte>(16 * 1024);
        int b;
        while ((b = stream.ReadByte()) >= 0 && b != '\n')
        {
            bytes.Add((byte)b);
            if (bytes.Count > MaxHeaderBytes)
            {
                throw new InvalidDataException("CSV header line is longer than 1 MB; not an NPPES file?");
            }
        }

        if (b < 0)
        {
            throw new InvalidDataException("CSV file has no complete header line.");
        }

        var crlf = bytes.Count > 0 && bytes[^1] == '\r';
        var line = Encoding.UTF8.GetString(bytes.ToArray(), 0, bytes.Count - (crlf ? 1 : 0));
        return new CsvHeader(ParseLine(line), crlf ? "\r\n" : "\n");
    }

    /// <summary>Splits one CSV line. Fields may be enclosed in double quotes; "" inside quotes is a literal quote.</summary>
    public static IReadOnlyList<string> ParseLine(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        if (inQuotes)
        {
            throw new InvalidDataException("Unterminated quoted field in CSV header.");
        }

        fields.Add(field.ToString());
        return fields;
    }
}
