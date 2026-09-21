FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

COPY Practical.MicrosoftAgentFramework.FoundryHostedAgent.csproj ./
RUN dotnet restore Practical.MicrosoftAgentFramework.FoundryHostedAgent.csproj

COPY . ./
RUN dotnet publish Practical.MicrosoftAgentFramework.FoundryHostedAgent.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore

FROM mcr.microsoft.com/powershell:7.5-ubuntu-24.04 AS powershell

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
RUN apt-get update \
    && apt-get install --yes --no-install-recommends dnsutils \
    && rm -rf /var/lib/apt/lists/*

COPY --from=powershell /opt/microsoft/powershell/7 /opt/microsoft/powershell/7
RUN ln -s /opt/microsoft/powershell/7/pwsh /usr/local/bin/pwsh

WORKDIR /app
COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8088
EXPOSE 8088

ENTRYPOINT ["dotnet", "Practical.MicrosoftAgentFramework.FoundryHostedAgent.dll"]
