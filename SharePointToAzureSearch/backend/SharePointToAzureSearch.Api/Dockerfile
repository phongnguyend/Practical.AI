FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY ["backend/SharePointToAzureSearch.Domain/SharePointToAzureSearch.Domain.csproj", "backend/SharePointToAzureSearch.Domain/"]
COPY ["backend/SharePointToAzureSearch.Application/SharePointToAzureSearch.Application.csproj", "backend/SharePointToAzureSearch.Application/"]
COPY ["backend/SharePointToAzureSearch.Persistence/SharePointToAzureSearch.Persistence.csproj", "backend/SharePointToAzureSearch.Persistence/"]
COPY ["backend/SharePointToAzureSearch.Infrastructure/SharePointToAzureSearch.Infrastructure.csproj", "backend/SharePointToAzureSearch.Infrastructure/"]
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
