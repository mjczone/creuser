# Creuser Docker Image Variants

> **Purpose:** Help operators choose between `creuser:latest` and `creuser:slim`, and understand the tradeoffs.
> **Last updated:** 2026-05-01

Creuser ships two image variants from the same Dockerfile, controlled by a build argument. Both variants share the same .NET 10 ASP.NET Core application, the same database schema, the same configuration model, and the same upgrade path. They differ only in what host-OS tooling is pre-installed in the image.

## Variant overview

| | `creuser:latest` (fat) | `creuser:slim` |
| --- | --- | --- |
| Approximate size | ~2.0 GB | ~600 MB |
| Cold pull time on a fast connection | ~30–60s | ~10–20s |
| Memory footprint at idle | ~250 MB | ~200 MB |
| .NET 10 SDK | ✅ | ✅ |
| Node 24 LTS | ✅ | ❌ |
| Python 3.13 + uv | ✅ | ❌ |
| git, ripgrep, fd, jq, yq, tree, bat | ✅ | ✅ (subset: git only) |
| ast-grep, tree-sitter, srgn | ✅ | ❌ |
| psql, redis-cli, sqlite3 | ✅ | ❌ |
| atlas, dbmate, migra | ✅ | ❌ |
| delta, difft, diff-so-fancy | ✅ | ❌ |
| Default for `docker pull mjczone/creuser` | ✅ | needs explicit `:slim` tag |

The size difference is mostly the polyglot language runtimes (~1 GB combined) plus the curated tool palette (~400 MB combined). The .NET application itself is the same in both.

## Choose `:latest` when…

**You want agent flexibility and developer ergonomics out of the box.** This is the scenario Creuser was designed for. Agents can run shell tools, invoke `psql` against the database to investigate query issues, parse JSON with `jq`, do structural code search with `ast-grep`, generate migration diffs with `atlas`, run a quick Python script the LLM wrote to crunch some data — all without any setup beyond the image pull. Developers writing job scripts can use whatever language they're most productive in.

**You're not sure what tools you'll need yet.** During initial deployment and exploration, the friction of "I need to update my Dockerfile and rebuild because I want to use ripgrep in this agent" compounds. The fat image lets you discover what works without cycle time.

**You're deploying to a managed platform (Railway, Render, Fly.io, AWS App Runner) where layer caching and image-pull bandwidth aren't bottlenecks.** Modern platforms cache base layers aggressively, so the 2 GB image effectively becomes a 1.4 GB delta on the first pull and ~50 MB on subsequent pushes (just the application layer changes). Image size matters less than people expect.

**You have plugins that depend on host-OS tools.** Most consumer-style plugins will assume the polyglot runtime is available. If you don't know whether your plugins need Python or Node, default to fat.

**Single-tenant on-premise deployment with twelve users on a trusted network.** This is Creuser's primary deployment shape. The threat model doesn't make the larger image meaningfully riskier — there's no attack surface delta that matters at this scale.

## Choose `:slim` when…

**You're confident your agents and job scripts will only ever invoke .NET runtimes.** If your implementation is a pure C# scripting environment with no Python data tools, no Node-based linters, no `jq` invocations from agents, slim is appropriate. This is rarer than it sounds — most real workflows reach for at least one polyglot tool eventually.

**You're running in a tightly resource-constrained environment.** Think edge deployments, IoT contexts, small VMs with <2 GB of disk after the OS, scenarios where every megabyte counts. Creuser isn't really designed for these contexts, but if you find yourself there, slim works.

**You're building a derivative image with custom tooling.** If you're going to write your own Dockerfile anyway (e.g. `FROM ghcr.io/mjczone/creuser:slim` and then `apt-get install` your specific tools), starting from slim avoids carrying tools you'll never use. This is a legitimate pattern for organizations with strict tool curation policies.

**You have a hard security mandate against unused runtimes in production images.** Some compliance regimes treat any installed-but-unused runtime as attack surface, regardless of actual exploitability. If you're operating under one of these, slim plus a custom layer is the right approach.

**Air-gapped or restricted-bandwidth deployment.** The smaller image transfers more easily over slow or metered connections. If you're shipping a USB drive into a SCIF, slim is the better starting point.

## Plugin compatibility

Plugins declare their runtime requirements in their manifest:

```yaml
# plugin manifest fragment
required_runtimes:
  - dotnet>=10.0
  - python>=3.12
  - node>=22
required_tools:
  - git
  - ripgrep
  - atlas>=0.30
```

When Creuser starts, it inspects loaded plugins against the available environment. If a plugin requires `python>=3.12` and the running image is `:slim`, Creuser logs a clear startup error and refuses to load that plugin (other plugins continue loading normally). The dashboard's plugin status page surfaces this so operators can see exactly why a plugin is unavailable.

This means you can move from slim to fat (or vice versa) without breaking anything — plugins gracefully appear and disappear based on what's available. Configuration, workflows, and data are unaffected.

## Switching between variants

Switching is a tag change in your `docker-compose.yml`:

```yaml
services:
  creuser:
    image: ghcr.io/mjczone/creuser:slim     # was :latest
```

Then `docker compose pull && docker compose up -d`. Database state, persistent volume contents, branding configuration, workspaces, runs history — all preserved. The only difference operators see post-restart is which plugins load successfully.

The reverse direction (slim → latest) works the same way and re-enables any plugins that were disabled due to missing runtimes.

## Building a custom variant

The Dockerfile uses a build argument:

```bash
# Build the fat image
docker build --build-arg VARIANT=fat -t creuser:custom-fat .

# Build the slim image
docker build --build-arg VARIANT=slim -t creuser:custom-slim .

# Build a derivative starting from slim, adding only the tools you need
cat <<EOF > Dockerfile.custom
FROM ghcr.io/mjczone/creuser:slim
RUN apt-get update && apt-get install -y \
    ripgrep jq python3 \
    && rm -rf /var/lib/apt/lists/*
EOF
docker build -t my-org/creuser:custom -f Dockerfile.custom .
```

The custom-derivative pattern is the recommended approach for organizations with specific tool requirements that don't match either default. Start from slim, add exactly what you need, and you get a smaller image than fat without giving up functionality you actually use.

## Resource sizing recommendations

For a single-tenant on-premise deployment with the typical Creuser workload:

| Variant | Min | Recommended | Headroom for active agents |
|---|---|---|---|
| `:latest` | 2 GB RAM, 2 vCPU | 4 GB RAM, 4 vCPU | 8 GB RAM, 8 vCPU |
| `:slim` | 1.5 GB RAM, 2 vCPU | 3 GB RAM, 4 vCPU | 6 GB RAM, 8 vCPU |

Postgres and Redis run as separate containers and have their own sizing concerns. The numbers above are for the Creuser application container only. Add ~2 GB RAM and ~20 GB disk for Postgres in a typical deployment, and ~512 MB RAM for Redis.

The "headroom for active agents" column matters because LLM tool loops can spawn multiple subprocesses (Python data processing, ast-grep across large repos, etc.) that each consume real memory. If you're running ten concurrent agentic workflows, the 8 GB tier is a safer landing.

## Tag conventions

Both variants follow the same versioning scheme:

| Tag | Meaning |
|---|---|
| `creuser:latest` | Most recent stable release, fat variant |
| `creuser:slim` | Most recent stable release, slim variant |
| `creuser:0.1.4` | Specific version, fat variant |
| `creuser:0.1.4-slim` | Specific version, slim variant |
| `creuser:edge` | Most recent build from `main` branch, fat variant (GHCR only, **not** on Docker Hub) |
| `creuser:edge-slim` | Most recent build from `main` branch, slim variant (GHCR only) |
| `creuser:0.1` | Most recent patch in the 0.1.x line, fat variant |
| `creuser:0.1-slim` | Most recent patch in the 0.1.x line, slim variant |

The `edge` tags are useful for testing fixes that haven't been released yet, but should not be used in production. They're only available on `ghcr.io/mjczone/creuser`, not on Docker Hub.

For production deployments, pin to a specific patch version (`creuser:0.1.4` or `creuser:0.1.4-slim`) rather than `latest` or `slim`. This ensures reproducible deployments and gives you control over when upgrades happen.

## Quickstart by scenario

**Scenario: Trying Creuser for the first time, local laptop**

```yaml
services:
  creuser:
    image: ghcr.io/mjczone/creuser:latest
```

The fat image. Don't optimize before you understand what you need.

**Scenario: Production deployment to Railway / Render / Fly.io**

```yaml
services:
  creuser:
    image: ghcr.io/mjczone/creuser:0.1.4
```

The fat image, pinned to a version. Image size doesn't matter much on managed platforms; predictability matters more.

**Scenario: Air-gapped corporate deployment with strict tool curation**

```dockerfile
FROM ghcr.io/mjczone/creuser:0.1.4-slim
RUN apt-get update && apt-get install -y \
    ripgrep jq \
    && rm -rf /var/lib/apt/lists/*
```

Slim base, hand-curated additions. Reviewable, auditable, smaller than fat.

**Scenario: Testing a fix that hasn't been released**

```yaml
services:
  creuser:
    image: ghcr.io/mjczone/creuser:edge
```

Edge tag, GHCR only. Don't put this in production.

**Scenario: Resource-constrained edge deployment**

```yaml
services:
  creuser:
    image: ghcr.io/mjczone/creuser:0.1.4-slim
    deploy:
      resources:
        limits:
          memory: 2G
          cpus: '2'
```

Slim variant, tight resource limits. Be aware that complex agent workflows may not fit comfortably in this footprint.

## Frequently asked questions

**Q: Can I run `:latest` and `:slim` in the same docker-compose stack?**

There's no good reason to. They're the same application; you'd just be doubling your resource consumption.

**Q: Will my data survive a switch from `:latest` to `:slim`?**

Yes. All persistent state lives in the Postgres database, Redis, and the `/data` volume. The application container is stateless from a data perspective. Switch tags freely.

**Q: Can I write a Creuser plugin that works on both variants?**

Yes, as long as the plugin's `required_runtimes` and `required_tools` are honest. A plugin that requires `python>=3.12` will simply be unavailable when running on slim, and Creuser will surface this clearly.

**Q: Why isn't there a `:micro` variant with no .NET SDK?**

Some Creuser features (like the `csharp` job runner — file-based `dotnet run script.cs` — and ahead-of-time compilation of plugins) require the .NET SDK at runtime, not just the runtime. Removing the SDK would break too much functionality. The slim variant strikes a better balance.

**Q: Can I build my own variant with different defaults?**

Yes. The Dockerfile is in `docker/Dockerfile` in the repo, and the variant logic is exposed via build arguments. Fork it, change what you need, build your own image. LGPL-3.0 covers this use case explicitly.

**Q: Does the `:slim` image still include the agent toolset for AI workflows?**

The Microsoft.Extensions.AI integration and the ToolLoopRunner are part of the .NET application, not the OS layer. They're identical in both variants. What differs is what the agent's `run_shell` tool can invoke — slim agents can shell out to `git` and not much else; fat agents have the full curated palette available.
