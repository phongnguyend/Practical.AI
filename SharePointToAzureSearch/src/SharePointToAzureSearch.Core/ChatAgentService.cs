using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;

namespace SharePointToAzureSearch.Core;

/// <summary>One assistant turn: what it said, and the documents it retrieved to say it.</summary>
public sealed record ChatTurn(string Text, IReadOnlyList<ChatCitation> Citations);

/// <summary>
/// The chat assistant. It runs on the Azure OpenAI chat deployment configured alongside the embedding
/// model and is given one tool — a hybrid search over the same index the rest of this solution fills —
/// so it answers from indexed SharePoint content instead of from the model's own memory.
/// </summary>
public sealed class ChatAgentService(
    ChatClient chatClient,
    ISearchQueryStore searchStore,
    ILogger<ChatAgentService> logger)
{
    /// <summary>
    /// How many turns of a conversation are replayed to the model. The whole history would grow past
    /// the context window on a long conversation; the most recent turns are what a follow-up question
    /// actually depends on.
    /// </summary>
    private const int MaxHistoryMessages = 40;

    private const string Instructions = """
        You answer questions about a SharePoint document library that has been indexed into Azure AI Search.

        Use the search_documents tool whenever the question could be about the content of those documents,
        including follow-up questions that depend on an earlier answer. Search before answering rather than
        guessing, and search again with different wording if the first results look unhelpful.

        Ground every factual claim in what the tool returned, and name the documents you used. If the search
        returns nothing relevant, say plainly that the indexed documents do not cover it — do not fall back
        on general knowledge and present it as though it came from the library.

        For anything that is not about the documents — a greeting, a question about what you can do — just
        answer normally without searching. Keep answers concise and use Markdown for structure.
        """;

    public async Task<ChatTurn> RunAsync(
        IReadOnlyList<ChatMessageRecord> history,
        string userMessage,
        string? userId,
        CancellationToken cancellationToken)
    {
        // The tool collects what it retrieved so the citations can be stored with the answer.
        var tool = new SearchTool(searchStore, userId, logger);

        var agent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "SharePointSearchAgent",
            ChatOptions = new ChatOptions
            {
                Instructions = Instructions,
                Tools = [AIFunctionFactory.Create(tool.SearchDocumentsAsync)],
            },
        });

        var messages = history
            .TakeLast(MaxHistoryMessages)
            .Select(x => new AIChatMessage(
                x.Role == ChatMessageRole.User ? AIChatRole.User : AIChatRole.Assistant,
                x.Content))
            .Append(new AIChatMessage(AIChatRole.User, userMessage))
            .ToList();

        // A fresh session each turn: the conversation lives in SQL Server and is replayed above, so the
        // agent needs no memory of its own and nothing has to be kept alive between requests.
        var session = await agent.CreateSessionAsync(cancellationToken);
        var response = await agent.RunAsync(messages, session, options: null, cancellationToken);

        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            text = "The model returned an empty response. Try rephrasing the question.";
        }

        logger.LogInformation(
            "Chat turn answered with {Searches} search call(s) and {Citations} citation(s).",
            tool.SearchCount,
            tool.Citations.Count);

        return new ChatTurn(text, tool.Citations);
    }

    /// <summary>
    /// The one tool the agent gets. It is an instance rather than a static function so that the user
    /// whose permissions apply, and the documents retrieved, belong to a single turn.
    /// </summary>
    private sealed class SearchTool(ISearchQueryStore store, string? userId, ILogger logger)
    {
        private readonly List<ChatCitation> _citations = [];

        public IReadOnlyList<ChatCitation> Citations => _citations;

        public int SearchCount { get; private set; }

        [Description("Search the indexed SharePoint documents and return the most relevant excerpts. Use this before answering anything about document content.")]
        public async Task<IReadOnlyList<SearchToolHit>> SearchDocumentsAsync(
            [Description("What to look for, in natural language. Prefer the user's own wording plus any clarifying terms.")]
            string query,
            [Description("How many excerpts to return, 1 to 10. Use 5 unless the question needs broader coverage.")]
            int top = 5,
            CancellationToken cancellationToken = default)
        {
            SearchCount++;

            // Hybrid retrieval: keyword matching finds exact names and identifiers, the vector side finds
            // passages that mean the same thing in different words.
            var request = new SearchQueryRequest(query, userId, Math.Clamp(top, 1, 10), 0);
            var results = await store.SearchAsync(SearchQueryMode.Hybrid, request, cancellationToken);

            var hits = new List<SearchToolHit>(results.Items.Count);
            foreach (var item in results.Items)
            {
                hits.Add(new SearchToolHit(item.Name, item.Path, item.ChunkNumber, item.Content));

                // One citation per file: several chunks of the same document are one source to a reader.
                if (!_citations.Any(x => x.Name == item.Name && x.ChunkNumber == item.ChunkNumber))
                {
                    _citations.Add(new ChatCitation(item.Name, item.Path, item.WebUrl, item.ChunkNumber, item.Score));
                }
            }

            logger.LogInformation("Agent searched for {Query} and got {Count} excerpts.", query, hits.Count);
            return hits;
        }
    }

    /// <summary>What the model sees for each excerpt. Deliberately small — no vectors, no identifiers.</summary>
    public sealed record SearchToolHit(string FileName, string? Folder, int ChunkNumber, string Excerpt);
}
