# syntax=docker/dockerfile:1.7
#
# Creuser production Dockerfile.
#
# Build args:
#   VARIANT  fat (default) | slim   — see docs/docker-variants.md
#
# Build context: the repo root (we COPY from src/, tests/, etc.). Run from
# repo root with:
#
#   docker build -f docker/Dockerfile --build-arg VARIANT=fat -t creuser:fat .
#   docker build -f docker/Dockerfile --build-arg VARIANT=slim -t creuser:slim .

ARG VARIANT=fat

# ─── Stage 1: build everything (.NET + SPA) ──────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Node 24 — needed for SPA build + hey-api codegen.
RUN curl -fsSL https://deb.nodesource.com/setup_24.x | bash - \
 && apt-get install -y --no-install-recommends nodejs \
 && rm -rf /var/lib/apt/lists/*

# Restore .NET deps first so layer caches survive code-only changes.
COPY Creuser.slnx global.json dotnet-tools.json ./
COPY src/Creuser.Core/Creuser.Core.csproj                                    src/Creuser.Core/
COPY src/Creuser.Persistence/Creuser.Persistence.csproj                      src/Creuser.Persistence/
COPY src/Creuser.Git/Creuser.Git.csproj                                      src/Creuser.Git/
COPY src/Creuser.Scripting/Creuser.Scripting.csproj                          src/Creuser.Scripting/
COPY src/Creuser.Agents/Creuser.Agents.csproj                                src/Creuser.Agents/
COPY src/Creuser.Auth.Abstractions/Creuser.Auth.Abstractions.csproj          src/Creuser.Auth.Abstractions/
COPY src/Creuser.Auth.Core/Creuser.Auth.Core.csproj                          src/Creuser.Auth.Core/
COPY src/Creuser.Auth.Providers.Local/Creuser.Auth.Providers.Local.csproj    src/Creuser.Auth.Providers.Local/
COPY src/Creuser.Auth.Providers.Google/Creuser.Auth.Providers.Google.csproj  src/Creuser.Auth.Providers.Google/
COPY src/Creuser.Web/Creuser.Web.csproj                                      src/Creuser.Web/
COPY tests/Creuser.Core.Tests/Creuser.Core.Tests.csproj                      tests/Creuser.Core.Tests/
COPY tests/Creuser.Integration.Tests/Creuser.Integration.Tests.csproj        tests/Creuser.Integration.Tests/
RUN dotnet restore Creuser.slnx

# Restore SPA deps separately (same caching reason).
COPY src/Creuser.Web.Spa/package.json src/Creuser.Web.Spa/package-lock.json src/Creuser.Web.Spa/
RUN cd src/Creuser.Web.Spa && npm ci --no-audit --no-fund

# Now bring in the rest of the source.
COPY . .

# Build .NET first so Microsoft.Extensions.ApiDescription.Server emits
# Creuser.Web.json (consumed by hey-api codegen below).
RUN dotnet build src/Creuser.Web/Creuser.Web.csproj -c Release --no-restore

# Generate the typed TS client and build the SPA into Creuser.Web/wwwroot.
RUN cd src/Creuser.Web.Spa \
 && npm run codegen \
 && npm run build

# Final publish — wwwroot now populated, gets bundled by Web SDK.
RUN dotnet publish src/Creuser.Web/Creuser.Web.csproj \
    -c Release -o /app --no-restore /p:UseAppHost=false

# ─── Stage 2: slim runtime ───────────────────────────────────────────────
# ASP.NET Core runtime + git only. Used when operators want a small image
# and curate their own tools in a derivative Dockerfile.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime-slim
RUN apt-get update && apt-get install -y --no-install-recommends \
      git ca-certificates \
 && rm -rf /var/lib/apt/lists/*

# ─── Stage 3: fat runtime ────────────────────────────────────────────────
# Full .NET SDK (some Creuser features need it at runtime — file-based C#
# scripts, plugin compilation) + the curated polyglot tool palette.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS runtime-fat
RUN apt-get update && apt-get install -y --no-install-recommends \
      git ripgrep fd-find jq tree bat ca-certificates \
      python3 python3-pip \
 && curl -fsSL https://deb.nodesource.com/setup_24.x | bash - \
 && apt-get install -y --no-install-recommends nodejs \
 && rm -rf /var/lib/apt/lists/*
# Note: ast-grep, atlas, dbmate, migra, srgn etc. are added in v0.1+ as
# the corresponding job-runner / agent features land. See docs/docker-variants.md.

# ─── Final stage: select via VARIANT ─────────────────────────────────────
FROM runtime-${VARIANT} AS final
WORKDIR /app
COPY --from=build /app .

EXPOSE 8080
VOLUME ["/data"]
ENV CREUSER_DATA_DIR=/data \
    PORT=8080

# Shell-form entrypoint so $PORT is resolved at runtime. Lets managed
# platforms (Railway, Render, Fly.io, Heroku, App Runner) inject their own
# port via the PORT env var without rebuilding the image. Compose / direct
# `docker run` get the 8080 default.
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet Creuser.Web.dll --urls=http://+:${PORT}"]
