FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY ["backend/SharePointToAzureSearch.Domain/SharePointToAzureSearch.Domain.csproj", "backend/SharePointToAzureSearch.Domain/"]
COPY ["backend/SharePointToAzureSearch.Application/SharePointToAzureSearch.Application.csproj", "backend/SharePointToAzureSearch.Application/"]
COPY ["backend/SharePointToAzureSearch.Persistence/SharePointToAzureSearch.Persistence.csproj", "backend/SharePointToAzureSearch.Persistence/"]
COPY ["backend/SharePointToAzureSearch.Infrastructure/SharePointToAzureSearch.Infrastructure.csproj", "backend/SharePointToAzureSearch.Infrastructure/"]
COPY ["backend/SharePointToAzureSearch.AgentHost/SharePointToAzureSearch.AgentHost.csproj", "backend/SharePointToAzureSearch.AgentHost/"]
RUN dotnet restore "backend/SharePointToAzureSearch.AgentHost/SharePointToAzureSearch.AgentHost.csproj"
COPY backend/SharePointToAzureSearch.Domain/ backend/SharePointToAzureSearch.Domain/
COPY backend/SharePointToAzureSearch.Application/ backend/SharePointToAzureSearch.Application/
COPY backend/SharePointToAzureSearch.Persistence/ backend/SharePointToAzureSearch.Persistence/
COPY backend/SharePointToAzureSearch.Infrastructure/ backend/SharePointToAzureSearch.Infrastructure/
COPY backend/SharePointToAzureSearch.AgentHost/ backend/SharePointToAzureSearch.AgentHost/
RUN dotnet publish "backend/SharePointToAzureSearch.AgentHost/SharePointToAzureSearch.AgentHost.csproj" \
    --configuration Release --output /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8088
EXPOSE 8088
USER $APP_UID
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "SharePointToAzureSearch.AgentHost.dll"]
