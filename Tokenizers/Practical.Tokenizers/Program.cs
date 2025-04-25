using Microsoft.ML.Tokenizers;

Tokenizer tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");

var input = "Chào anh em .Net Hội";

var count = tokenizer.CountTokens(input);

Console.WriteLine($"Number of Tokens: {count}");

var tokens = tokenizer.EncodeToTokens(input, out string? normalizedText);

foreach (var token in tokens)
{
    Console.WriteLine($"[{token.Id}, ({token.Offset.Start}, {token.Offset.End}), '{token.Value}']");
}
