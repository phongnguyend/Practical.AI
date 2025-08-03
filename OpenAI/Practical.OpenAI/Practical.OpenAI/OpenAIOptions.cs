using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

namespace Practical.OpenAI;

public class OpenAIOptions
{
    public string Endpoint { get; set; }

    public string ApiKey { get; set; }

    public string ModelId { get; set; }

    public ChatClient CreateChatClient()
    {
        if (string.IsNullOrEmpty(ApiKey))
            throw new ArgumentException("API Key is required", nameof(ApiKey));

        if (string.IsNullOrEmpty(Endpoint))
            throw new ArgumentException("Endpoint is required", nameof(Endpoint));

        if (string.IsNullOrEmpty(ModelId))
            throw new ArgumentException("ModelId is required", nameof(ModelId));

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(Endpoint)
        };

        return new ChatClient(ModelId, new ApiKeyCredential(ApiKey), options);
    }
}
