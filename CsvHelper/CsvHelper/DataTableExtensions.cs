using System.Data;
using System.Text;

namespace CsvHelper;

public static class DataTableExtensions
{
    public static string ToCsv(this DataTable table)
    {
        using var stream = new MemoryStream();
        table.ToCsv(stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static void ToCsv(this DataTable table, Stream stream)
    {
        using var writer = new StreamWriter(stream, Encoding.UTF8);

        var line = new StringBuilder();

        // Add headers
        for (int i = 0; i < table.Columns.Count; i++)
        {
            line.Append(Escape(table.Columns[i].ColumnName));

            if (i < table.Columns.Count - 1)
            {
                line.Append(',');
            }
        }
        writer.WriteLine(line.ToString());

        // Add rows
        foreach (DataRow row in table.Rows)
        {
            line.Clear();

            for (int i = 0; i < table.Columns.Count; i++)
            {
                line.Append(Escape(row[i]?.ToString()));

                if (i < table.Columns.Count - 1)
                {
                    line.Append(',');
                }
            }

            writer.WriteLine(line.ToString());
        }
    }

    private static string Escape(string value)
    {
        return CsvWriter.Escape(value);
    }
}
