using System.ComponentModel;

namespace Practical.MicrosoftAgentFramework.FoundryHostedAgent;

internal sealed class SandboxStateFunctions
{
    private const string EmptyStateMessage = "No sandbox state has been written in this session.";
    private readonly string _stateFilePath;

    public SandboxStateFunctions(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        _stateFilePath = Path.Combine(stateDirectory, "sandbox_state.txt");
    }

    [Description("Write or replace the test state stored in a local file inside the current Foundry session sandbox. Use this tool whenever the user asks to save sandbox state for a session-isolation test.")]
    public async Task<string> WriteSandboxStateAsync(
        [Description("The exact state value to persist in the current session sandbox.")] string state,
        CancellationToken cancellationToken = default)
    {
        await File.WriteAllTextAsync(_stateFilePath, state, cancellationToken);
        return $"Sandbox state written: {state}";
    }

    [Description("Read the test state from the local file inside the current Foundry session sandbox. Use this tool whenever the user asks to read or verify sandbox state.")]
    public async Task<string> ReadSandboxStateAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_stateFilePath))
        {
            return EmptyStateMessage;
        }

        return await File.ReadAllTextAsync(_stateFilePath, cancellationToken);
    }

    [Description("Delete the test state file from the current Foundry session sandbox.")]
    public Task<string> ClearSandboxStateAsync()
    {
        if (!File.Exists(_stateFilePath))
        {
            return Task.FromResult(EmptyStateMessage);
        }

        File.Delete(_stateFilePath);
        return Task.FromResult("Sandbox state cleared.");
    }
}
