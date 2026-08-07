FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY ["src/SharePointToAzureSearch.Core/SharePointToAzureSearch.Core.csproj", "src/SharePointToAzureSearch.Core/"]
COPY ["src/SharePointToAzureSearch.Background/SharePointToAzureSearch.Background.csproj", "src/SharePointToAzureSearch.Background/"]
RUN dotnet restore "src/SharePointToAzureSearch.Background/SharePointToAzureSearch.Background.csproj"

COPY . .
RUN dotnet publish "src/SharePointToAzureSearch.Background/SharePointToAzureSearch.Background.csproj" \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
USER $APP_UID
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "SharePointToAzureSearch.Background.dll"]
