namespace CsvHelper.Tests;

public class CsvReaderTests
{
    [Fact]
    public void Read_ReturnsExpectedData()
    {
        var rows = new List<string[]>
        {
            new[] { "Name", "Comment" },
            new[] { "Alice", "Hello, world!" },
            new[] { "Bob", "He said, \"Hi!\"" },
            new[] { "Charlie", "New\nLine" }
        };

        CsvWriter.Write("test.csv", rows);

        var readData = CsvReader.Read("test.csv");

        Assert.Equal(4, readData.Count);
        Assert.Equal("Name", readData[0][0]);
        Assert.Equal("Comment", readData[0][1]);
        Assert.Equal("Alice", readData[1][0]);
        Assert.Equal("Hello, world!", readData[1][1]);
        Assert.Equal("Bob", readData[2][0]);
        Assert.Equal("He said, \"Hi!\"", readData[2][1]);
        Assert.Equal("Charlie", readData[3][0]);
        Assert.Equal("New\nLine", readData[3][1]);
    }
}