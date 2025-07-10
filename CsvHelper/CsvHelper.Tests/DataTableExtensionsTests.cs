using System.Data;
using System.Text;

namespace CsvHelper.Tests;

public class DataTableExtensionsTests
{
    [Fact]
    public Task ToCsv_ShouldReturnCorrectCsvString()
    {
        // Arrange
        var table = new DataTable();

        table.Columns.Add("Name");
        table.Columns.Add("Age");
        table.Columns.Add("City");
        table.Rows.Add("Alice", 30, "New York");
        table.Rows.Add("Bob", 25, "Los Angeles");
        table.Rows.Add("Charlie", 35, "Chicago");

        // Act
        var csv = table.ToCsv();

        // Assert
        return Verify(csv);
    }

    [Fact]
    public Task ToCsv_ShouldWriteCsvToStream()
    {
        // Arrange
        var table = new DataTable();

        table.Columns.Add("Name");
        table.Columns.Add("Age");
        table.Columns.Add("City");
        table.Rows.Add("Alice", 30, "New York");
        table.Rows.Add("Bob", 25, "Los Angeles");
        table.Rows.Add("Charlie", 35, "Chicago");

        using var stream = new MemoryStream();

        // Act
        table.ToCsv(stream);

        var csv = Encoding.UTF8.GetString(stream.ToArray());

        // Assert
        return Verify(csv);
    }
}
