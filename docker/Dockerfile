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
# Pin to the same patch as `global.json` so a rolling-tag bump on the
# upstream image can't break the build without warning. Bump in lockstep
# with `global.json` when upgrading the SDK.
FROM mcr.microsoft.com/dotnet/sdk:10.0.102 AS build
WORKDIR /src

# Quiet down the .NET first-run experience and disable the diagnostic
# listener. The diagnostic listener spawns a thread that can interact
# poorly with BuildKit's seccomp profile and surface as opaque
# signal-derived exit codes (e.g. 155 = 128 + 27 = SIGPROF) during long
# `dotnet restore` runs.
ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    NUGET_XMLDOC_MODE=skip

# Node 24 — needed for SPA build + hey-api codegen.
RUN curl -fsSL https://deb.nodesource.com/setup_24.x | bash - \
 && apt-get install -y --no-install-recommends nodejs \
 && rm -rf /var/lib/apt/lists/*

# Restore .NET deps first so layer caches survive code-only changes.
# `nuget.config` pins the package source to nuget.org with `<clear/>` so
# nothing from the host's NuGet config leaks into the build (matters even
# in the official SDK image — the OS-level NuGet.Config can ship with
# additional sources or fallback rules).
COPY Creuser.slnx global.json dotnet-tools.json nuget.config ./
COPY src/Creuser.Core/Creuser.Core.csproj                                                src/Creuser.Core/
COPY src/Creuser.Persistence/Creuser.Persistence.csproj                                  src/Creuser.Persistence/
COPY src/Creuser.Git/Creuser.Git.csproj                                                  src/Creuser.Git/
COPY src/Creuser.Scripting/Creuser.Scripting.csproj                                      src/Creuser.Scripting/
COPY src/Creuser.Agents/Creuser.Agents.csproj                                            src/Creuser.Agents/
COPY src/Creuser.Sagas/Creuser.Sagas.csproj                                              src/Creuser.Sagas/
COPY src/Creuser.Projections/Creuser.Projections.csproj                                  src/Creuser.Projections/
COPY src/Creuser.Plugins.Abstractions/Creuser.Plugins.Abstractions.csproj                src/Creuser.Plugins.Abstractions/
COPY src/Creuser.Plugins.Loader/Creuser.Plugins.Loader.csproj                            src/Creuser.Plugins.Loader/
COPY src/Creuser.Auth.Abstractions/Creuser.Auth.Abstractions.csproj                      src/Creuser.Auth.Abstractions/
COPY src/Creuser.Auth.Core/Creuser.Auth.Core.csproj                                      src/Creuser.Auth.Core/
COPY src/Creuser.Auth.Providers.Local/Creuser.Auth.Providers.Local.csproj                src/Creuser.Auth.Providers.Local/
COPY src/Creuser.Auth.Providers.Google/Creuser.Auth.Providers.Google.csproj              src/Creuser.Auth.Providers.Google/
COPY src/Creuser.Web/Creuser.Web.csproj                                                  src/Creuser.Web/
COPY src/plugins/Creuser.Plugins.Examples.Hello/Creuser.Plugins.Examples.Hello.csproj                src/plugins/Creuser.Plugins.Examples.Hello/
COPY src/plugins/Creuser.Plugins.Examples.Slack/Creuser.Plugins.Examples.Slack.csproj                src/plugins/Creuser.Plugins.Examples.Slack/
COPY src/plugins/Creuser.Plugins.Examples.GitHubTools/Creuser.Plugins.Examples.GitHubTools.csproj    src/plugins/Creuser.Plugins.Examples.GitHubTools/
COPY tests/Creuser.Core.Tests/Creuser.Core.Tests.csproj                                  tests/Creuser.Core.Tests/
COPY tests/Creuser.Scripting.Tests/Creuser.Scripting.Tests.csproj                        tests/Creuser.Scripting.Tests/
COPY tests/Creuser.Projections.Tests/Creuser.Projections.Tests.csproj                    tests/Creuser.Projections.Tests/
COPY tests/Creuser.Integration.Tests/Creuser.Integration.Tests.csproj                    tests/Creuser.Integration.Tests/
RUN dotnet restore Creuser.slnx

# Restore SPA deps separately (same caching reason).
# `--ignore-scripts` skips lifecycle hooks — the SPA's `postinstall` runs
# `quasar prepare`, which needs `quasar.config.ts` and friends that
# haven't been copied yet at this layer (the full source comes in below
# via `COPY . .`). We run `quasar prepare` explicitly after that copy so
# the cached `npm ci` layer survives code-only changes.
COPY src/Creuser.Web.Spa/package.json src/Creuser.Web.Spa/package-lock.json src/Creuser.Web.Spa/
RUN cd src/Creuser.Web.Spa && npm ci --ignore-scripts --no-audit --no-fund

# Now bring in the rest of the source.
COPY . .

# Run the postinstall hook now that the Quasar project files exist.
# (Equivalent to what `npm ci` would have done if --ignore-scripts
# weren't set above.)
RUN cd src/Creuser.Web.Spa && npx quasar prepare

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
# .NET 10 SDK + git only. Slim still needs the SDK (not just the runtime)
# because Creuser features like the `csharp` file-based job runner
# (`dotnet run script.cs`) and AOT plugin compilation invoke the SDK at
# runtime — see the "Why isn't there a :micro variant" FAQ in
# docs/docker-variants.md. Operators wanting a smaller no-SDK base
# should derive their own image from `aspnet:10.0` and accept that those
# features will be unavailable.
FROM mcr.microsoft.com/dotnet/sdk:10.0.102 AS runtime-slim
RUN apt-get update && apt-get install -y --no-install-recommends \
      git openssh-client ca-certificates \
 && rm -rf /var/lib/apt/lists/*

# ─── Stage 3: fat runtime ────────────────────────────────────────────────
# Full .NET SDK (some Creuser features need it at runtime — file-based C#
# scripts, plugin compilation) + the curated polyglot tool palette.
FROM mcr.microsoft.com/dotnet/sdk:10.0.102 AS runtime-fat
RUN apt-get update && apt-get install -y --no-install-recommends \
      git openssh-client ripgrep fd-find jq tree bat ca-certificates \
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
