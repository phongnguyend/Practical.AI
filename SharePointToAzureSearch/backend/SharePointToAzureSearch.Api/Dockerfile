FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY ["backend/SharePointToAzureSearch.Core/SharePointToAzureSearch.Core.csproj", "backend/SharePointToAzureSearch.Core/"]
COPY ["backend/SharePointToAzureSearch.Api/SharePointToAzureSearch.Api.csproj", "backend/SharePointToAzureSearch.Api/"]
RUN dotnet restore "backend/SharePointToAzureSearch.Api/SharePointToAzureSearch.Api.csproj"

COPY . .
RUN dotnet publish "backend/SharePointToAzureSearch.Api/SharePointToAzureSearch.Api.csproj" \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "SharePointToAzureSearch.Api.dll"]
