using Aspire.Hosting;

// Runs the three executable hosts together against a containerized SQL Server, so a developer gets the
// whole backend — API, indexing worker, and hosted chat agent — with one F5 and one dashboard.
//
// Only SQL Server is provisioned here. SharePoint, Azure AI Search, Azure OpenAI, Service Bus, and
// MarkItDown are configured per project in appsettings and user secrets, because they are tenant and
// subscription resources rather than things a container can stand in for.
var builder = DistributedApplication.CreateBuilder(args);

var sql = builder.AddSqlServer("sql")
    // The database outlives a debugging session, so an indexed library is not rebuilt on every run.
    .WithLifetime(ContainerLifetime.Persistent);

var database = sql.AddDatabase("sharepoint-index", "SharePointSearch");

var agentHost = builder.AddProject<Projects.SharePointToAzureSearch_AgentHost>("agenthost")
    .WithEnvironment("SqlServer__ConnectionString", database)
    .WaitFor(database);

builder.AddProject<Projects.SharePointToAzureSearch_Background>("background")
    .WithEnvironment("SqlServer__ConnectionString", database)
    .WaitFor(database);

builder.AddProject<Projects.SharePointToAzureSearch_Api>("api")
    .WithEnvironment("SqlServer__ConnectionString", database)
    // Read only when ChatAgent:Mode is Foundry, so it costs nothing in the default local mode and
    // makes switching to the hosted agent a one-setting change.
    .WithEnvironment(context => context.EnvironmentVariables["ChatAgent__Foundry__Endpoint"] =
        ReferenceExpression.Create($"{agentHost.GetEndpoint("http")}/invocations"))
    .WithExternalHttpEndpoints()
    .WaitFor(database);

builder.Build().Run();
