using System.Text;

namespace CsvHelper;

public class CsvReader
{
    public static List<string[]> Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static List<string[]> Read(Stream stream)
    {
        var result = new List<string[]>();
        using var reader = new StreamReader(stream);

        string? line;
        string? pendingLine = null;

        while ((line = reader.ReadLine()) != null)
        {
            // If we have a pending line (multiline field), combine it
            if (pendingLine != null)
            {
                pendingLine += "\n" + line;
            }
            else
            {
                pendingLine = line;
            }

            // Count quotes to check if we are inside a quoted field
            int quoteCount = 0;
            for (int i = 0; i < pendingLine.Length; i++)
            {
                if (pendingLine[i] == '"')
                {
                    quoteCount++;
                }
            }

            // If quote count is even, the record is complete
            if (quoteCount % 2 == 0)
            {
                result.Add(ParseCsvLine(pendingLine));
                pendingLine = null;
            }
            // Else, continue accumulating lines
        }

        return result;
    }

    private static string[] ParseCsvLine(string line)
    {
        var values = new List<string>();
        bool inQuotes = false;
        var value = new StringBuilder();

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') // Escaped quote
                    {
                        value.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    value.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    values.Add(value.ToString());
                    value.Clear();
                }
                else
                {
                    value.Append(c);
                }
            }
        }

        values.Add(value.ToString()); // Add last field
        return values.ToArray();
    }
}
