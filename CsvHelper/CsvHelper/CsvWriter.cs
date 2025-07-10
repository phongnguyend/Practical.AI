using System.Text;

namespace CsvHelper;

public class CsvWriter
{
    public static void Write(string path, List<string[]> rows)
    {
        using var stream = File.OpenWrite(path);
        Write(stream, rows);
    }

    public static void Write(Stream stream, List<string[]> rows)
    {
        var line = new StringBuilder();
        using var writer = new StreamWriter(stream, Encoding.UTF8);

        foreach (var row in rows)
        {
            line.Clear();

            for (int i = 0; i < row.Length; i++)
            {
                string field = Escape(row[i]);

                line.Append(field);

                if (i < row.Length - 1)
                {
                    line.Append(',');
                }
            }

            writer.WriteLine(line.ToString());
        }
    }

    public static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        // Escape if needed
        bool mustQuote = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        if (mustQuote)
        {
            value = "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }
}
