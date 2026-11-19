# FreightRAG build/test/run image. No .NET SDK is needed on the host.
# Images are pinned by tag AND digest (.NET 10.0.14 release, 2026-10-25) so builds are reproducible.
# The pins live directly in the FROM lines so Dependabot can update them (.github/dependabot.yml).

# ---- build: restore (cached layer) then compile everything ----
# --platform=$BUILDPLATFORM: compile on the machine's native architecture even when building an image
# for another one (CI publishes amd64 + arm64). The output is portable IL, so no emulation is needed.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.404-noble@sha256:bef136a371963aaa46ec97882845ef6f54dec15728bb4b128b1c2a400ffbc9b4 AS build
WORKDIR /repo
# Copy only the files that affect restore first, so `dotnet restore` is cached
# until a project file or package version changes.
COPY global.json ./
COPY src/Directory.Build.props src/Directory.Packages.props src/FreightRag.slnx src/
COPY src/FreightRag.Api/FreightRag.Api.csproj src/FreightRag.Api/
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
# The OpenAPI snapshot test compares the served document with docs/openapi/v1.json. Copied here, not in
# `build`, so editing docs never invalidates the compiled layers (or republishes unchanged images).
COPY docs/openapi/ docs/openapi/
ENTRYPOINT ["dotnet", "test", "--solution", "src/FreightRag.slnx", "-c", "Release", "--no-build"]

# ---- docs: DocFX site (C# reference from XML comments + articles + REST page) ----
FROM build AS docs
COPY .config/ .config/
COPY docs/ docs/
RUN dotnet tool restore && dotnet docfx docs/docfx.json --warningsAsErrors

# Just the generated site, for `docker build --target docs-site --output type=local,dest=...` (CI → Pages).
FROM scratch AS docs-site
COPY --from=docs /repo/docs/_site/ /

# ---- publish the HTTP API ----
FROM build AS publish-api
RUN dotnet publish src/FreightRag.Api/FreightRag.Api.csproj -c Release --no-build -o /app

# ---- publish the console app ----
FROM build AS publish
RUN dotnet publish src/FreightRag.Ingest/FreightRag.Ingest.csproj -c Release --no-build -o /app

# ---- ingest: small runtime image, non-root, no data or secrets baked in ----
FROM mcr.microsoft.com/dotnet/runtime:10.0.15-noble@sha256:64a538092439382d055c87c0f49092efb8bca883c736c54ae523da7fd8e02d18 AS ingest
WORKDIR /app
COPY --from=publish /app ./
# APP_UID is the built-in non-root "app" user of the official .NET images.
USER $APP_UID
ENTRYPOINT ["dotnet", "FreightRag.Ingest.dll"]
# Documents are bind-mounted read-only at /data (see compose.yaml).
CMD ["--data-dir", "/data"]

# ---- api: ASP.NET Core runtime image, non-root, listens on 8080 ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-noble@sha256:12b1135f5f8b3c0a214f2a680ff2033dff4f684f445d6c697d9524ceeb303586 AS api
WORKDIR /app
COPY --from=publish-api /app ./
USER $APP_UID
# The official image already sets ASPNETCORE_HTTP_PORTS=8080 (a non-privileged port).
EXPOSE 8080
ENTRYPOINT ["dotnet", "FreightRag.Api.dll"]
