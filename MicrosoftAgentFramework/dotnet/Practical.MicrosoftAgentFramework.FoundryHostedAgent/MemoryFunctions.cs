using System.ComponentModel;
using System.Text.Json;

namespace Practical.MicrosoftAgentFramework.FoundryHostedAgent;

internal class MemoryFunctions
{
    private readonly string _memoryFilePath;
    private readonly Dictionary<string, string> _memories;

    public MemoryFunctions(string memoryFilePath)
    {
        _memoryFilePath = memoryFilePath;
        _memories = Load();
    }

    [Description("Remember a fact about the user. Use a short descriptive key and the value to store.")]
    public Task RememberAsync(string key, string value)
    {
        _memories[key] = value;
        Save();
        return Task.CompletedTask;
    }

    [Description("Recall a previously remembered fact about the user by key. Returns null if not found.")]
    public Task<string?> RecallAsync(string key)
    {
        _memories.TryGetValue(key, out var value);
        return Task.FromResult(value);
    }

    [Description("List all remembered facts about the user as key-value pairs.")]
    public Task<Dictionary<string, string>> ListMemoriesAsync() =>
        Task.FromResult(new Dictionary<string, string>(_memories));

    [Description("Forget a previously remembered fact about the user by key.")]
    public Task ForgetAsync(string key)
    {
        _memories.Remove(key);
        Save();
        return Task.CompletedTask;
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_memoryFilePath))
        {
            return [];
        }

        var json = File.ReadAllText(_memoryFilePath);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_memories, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_memoryFilePath, json);
    }
}
