namespace MarkItDownDotNet.Server;

public static class FileService
{
    public static FileStream CreateTempFileStream(out string filePath)
    {
        string tempFilePath = CreateTempFilePath();

        filePath = tempFilePath;

        return CreateTempFileStream(tempFilePath);
    }

    public static string CreateTempFilePath()
    {
        string tempFilePath;
        do
        {
            tempFilePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        }
        while (File.Exists(tempFilePath));

        return tempFilePath;
    }

    public static FileStream CreateTempFileStream(string path)
    {
        return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
    }
}
