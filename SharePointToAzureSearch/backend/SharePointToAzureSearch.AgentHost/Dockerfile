FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY ["backend/SharePointToAzureSearch.Core/SharePointToAzureSearch.Core.csproj", "backend/SharePointToAzureSearch.Core/"]
COPY ["backend/SharePointToAzureSearch.AgentHost/SharePointToAzureSearch.AgentHost.csproj", "backend/SharePointToAzureSearch.AgentHost/"]
RUN dotnet restore "backend/SharePointToAzureSearch.AgentHost/SharePointToAzureSearch.AgentHost.csproj"
COPY backend/SharePointToAzureSearch.Core/ backend/SharePointToAzureSearch.Core/
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
