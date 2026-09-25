using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NSubstitute;
using SharePointToAzureSearch.Application;
using SharePointToAzureSearch.Domain;
using SharePointToAzureSearch.Infrastructure;
using SharePointToAzureSearch.Persistence;
using Xunit;

namespace SharePointToAzureSearch.Tests;

public sealed class ChatAgentContextTests
{
    [Fact]
    public async Task LoadsDatabaseSettingsAndOnlyFortyMessagesBeforeTheSavedQuestion()
    {
        var chats = Substitute.For<IChatRepository>();
        var agents = Substitute.For<IAgentRepository>();
        var id = Guid.NewGuid();
        var agent = new AgentDefinition(Guid.NewGuid(), "Document agent", "model-from-db", "instructions-from-db", default, default);
        var conversation = new ChatConversation(id, "Chat", "user-from-db", agent.Id, default, default, 53, 0, 0, 0);
        var messages = Enumerable.Range(0, 53).Select(i => Message(id, i.ToString())).ToArray();
        var question = messages[50];
        messages[49] = messages[49] with { Attachments = [new(Guid.NewGuid(), "document.docx", null, 10)] };
        chats.GetConversationAsync(id, default).Returns(conversation);
        chats.ListMessagesAsync(id, default).Returns(messages);
        agents.GetAsync(agent.Id, default).Returns(agent);

        var result = await new ChatAgentContextLoader(chats, agents).LoadAsync(new(id, question.Id), default);

        Assert.Equal(agent, result.Agent);
        Assert.Equal("user-from-db", result.Conversation.UserId);
        Assert.Equal(messages.Skip(10).Take(40), result.History);
        Assert.Single(result.History.Last().Attachments);
        Assert.Equal(question, result.Question);
        Assert.DoesNotContain(result.History, m => m.Id == question.Id || m.Id == messages[51].Id);
    }

    [Fact]
    public async Task RejectsQuestionNotInTheConversation()
    {
        var chats = Substitute.For<IChatRepository>();
        var agents = Substitute.For<IAgentRepository>();
        var id = Guid.NewGuid();
        chats.GetConversationAsync(id, default).Returns(new ChatConversation(id, "Chat", null, null, default, default, 0, 0, 0, 0));
        agents.GetByNameAsync(AgentDefaults.Name, default)
            .Returns(new AgentDefinition(Guid.NewGuid(), AgentDefaults.Name, "model", "instructions", default, default));
        chats.ListMessagesAsync(id, default).Returns(new[] { Message(id, "other question") });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ChatAgentContextLoader(chats, agents).LoadAsync(new(id, Guid.NewGuid()), default));
    }

    [Fact]
    public void SessionMigrationMatchesTheRuntimeModel()
    {
        using var db = new SharePointIndexDbContext(new DbContextOptionsBuilder<SharePointIndexDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var script = db.GetService<IMigrator>().GenerateScript("20260916170000_AddWebhookSubscriptionSettings");
        Assert.Contains("ADD [FoundryEndpoint] nvarchar(2048)", script);
        Assert.Contains("ADD [FoundrySessionId] nvarchar(200)", script);
    }

    private static ChatMessageRecord Message(Guid conversationId, string content) =>
        new(Guid.NewGuid(), conversationId, ChatMessageRole.User, content, [], 0, 0, 0, null, null, [], default);
}
