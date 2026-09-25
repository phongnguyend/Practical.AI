FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY ["backend/SharePointToAzureSearch.Core/SharePointToAzureSearch.Core.csproj", "backend/SharePointToAzureSearch.Core/"]
COPY ["backend/SharePointToAzureSearch.Background/SharePointToAzureSearch.Background.csproj", "backend/SharePointToAzureSearch.Background/"]
RUN dotnet restore "backend/SharePointToAzureSearch.Background/SharePointToAzureSearch.Background.csproj"

COPY . .
RUN dotnet publish "backend/SharePointToAzureSearch.Background/SharePointToAzureSearch.Background.csproj" \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
USER $APP_UID
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "SharePointToAzureSearch.Background.dll"]
