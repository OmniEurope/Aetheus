# Local-only Docker fixture for the Aetheus Toto pipeline E2E harness.
FROM mcr.microsoft.com/dotnet/sdk:10.0.403@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/Toto.Api/Toto.Api.csproj
RUN dotnet publish src/Toto.Api/Toto.Api.csproj --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build --chown=app:app /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Toto.Api.dll"]
