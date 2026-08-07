FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY ["src/SharePointToAzureSearch.Core/SharePointToAzureSearch.Core.csproj", "src/SharePointToAzureSearch.Core/"]
COPY ["src/SharePointToAzureSearch.Api/SharePointToAzureSearch.Api.csproj", "src/SharePointToAzureSearch.Api/"]
RUN dotnet restore "src/SharePointToAzureSearch.Api/SharePointToAzureSearch.Api.csproj"

COPY . .
RUN dotnet publish "src/SharePointToAzureSearch.Api/SharePointToAzureSearch.Api.csproj" \
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
