namespace SharePointToAzureSearch.Domain;

public static class TextChunker
{
    public static IReadOnlyList<string> Split(string text, int size, int overlap)
    {
        if (overlap >= size)
        {
            throw new ArgumentOutOfRangeException(nameof(overlap), "Overlap must be smaller than chunk size.");
        }

        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0)
        {
            return [""];
        }

        var chunks = new List<string>();
        for (var start = 0; start < text.Length; start += size - overlap)
        {
            var length = Math.Min(size, text.Length - start);
            chunks.Add(text.Substring(start, length));
            if (start + length >= text.Length)
            {
                break;
            }
        }
        return chunks;
    }
}
