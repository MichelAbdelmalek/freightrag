# FreightRAG build/test/run image. No .NET SDK is needed on the host.
# Images are pinned by tag AND digest (.NET 10.0.14 release, 2026-10-25) so builds are reproducible.
# The pins live directly in the FROM lines so Dependabot can update them (.github/dependabot.yml).

# ---- build: restore (cached layer) then compile everything ----
# --platform=$BUILDPLATFORM: compile on the machine's native architecture even when building an image
# for another one (CI publishes amd64 + arm64). The output is portable IL, so no emulation is needed.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.403-noble@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e AS build
WORKDIR /repo
# Copy only the files that affect restore first, so `dotnet restore` is cached
# until a project file or package version changes.
COPY global.json ./
COPY src/Directory.Build.props src/Directory.Packages.props src/FreightRag.slnx src/
COPY src/FreightRag.Core/FreightRag.Core.csproj src/FreightRag.Core/
COPY src/FreightRag.Ingest/FreightRag.Ingest.csproj src/FreightRag.Ingest/
COPY src/FreightRag.Tests/FreightRag.Tests.csproj src/FreightRag.Tests/
RUN dotnet restore src/FreightRag.slnx
COPY src/ src/
# The tests read the real synthetic documents.
COPY data/ data/
RUN dotnet build src/FreightRag.slnx -c Release --no-restore

# ---- test: `docker compose run --rm tests` ----
FROM build AS test
ENTRYPOINT ["dotnet", "test", "--solution", "src/FreightRag.slnx", "-c", "Release", "--no-build"]

# ---- publish the console app ----
FROM build AS publish
RUN dotnet publish src/FreightRag.Ingest/FreightRag.Ingest.csproj -c Release --no-build -o /app

# ---- ingest: small runtime image, non-root, no data or secrets baked in ----
FROM mcr.microsoft.com/dotnet/runtime:10.0.14-noble@sha256:4f1f30cf0bb4ab0c8b2daa84613051683456f8df4a9bf7c812e20d74962992ac AS ingest
WORKDIR /app
COPY --from=publish /app ./
# APP_UID is the built-in non-root "app" user of the official .NET images.
USER $APP_UID
ENTRYPOINT ["dotnet", "FreightRag.Ingest.dll"]
# Documents are bind-mounted read-only at /data (see compose.yaml).
CMD ["--data-dir", "/data"]
