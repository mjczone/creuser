# creuser

> ⚠️ **UNDER DEVELOPMENT - BREAKING CHANGES EXPECTED**
>
> This library is in active development (v0.x.x). The API is not yet stable and breaking changes may occur between releases. Do not use in production until v1.0.0 is released, unless you're working directly with MJCZone Inc.

**Get to the bottom of it!**

Creuser is an open-source workflow and agent orchestration platform for monorepo operations. Built by MJCZone.

Pronounced "KROO-ZAY" (or "kruh-ZAY" if you're French, or even better with an "é", just have fun with it).

---

## Getting Started

**With `docker compose` (recommended — includes Postgres + Redis):**

Copy [`docker/docker-compose.yml`](docker/docker-compose.yml) into a folder on your machine, then:

```bash
echo "POSTGRES_PASSWORD=$(openssl rand -hex 24)" > .env
docker compose up -d
```

Open <http://localhost:8080> and follow the bootstrap admin flow. Image variants (`:latest` vs `:slim`) are documented in [docs/docker-variants.md](docs/docker-variants.md).

**With `docker run` (single container — you provide Postgres + Redis):**

```bash
docker run -d --name creuser \
  -p 8080:8080 \
  -v creuser-data:/data \
  -e ConnectionStrings__Postgres="Host=<host>;Port=5432;Database=creuser;Username=creuser;Password=<password>" \
  -e ConnectionStrings__Redis="<host>:6379" \
  ghcr.io/mjczone/creuser:latest
```

**With HTTPS on a self-hosted VM (Caddy + Let's Encrypt):**

Point `creuser.example.com` at your host's IP, then:

```bash
echo "POSTGRES_PASSWORD=$(openssl rand -hex 24)" >  .env
echo "CREUSER_DOMAIN=creuser.example.com"        >> .env
docker compose -f docker/docker-compose.yml \
               -f docker/docker-compose.caddy.yml up -d
```

Caddy fetches a Let's Encrypt cert on first request and auto-renews it.

**On managed platforms (Railway, Render, Fly.io, Cloudflare Tunnel, App Runner):**

Either point the platform at [`docker/Dockerfile`](docker/Dockerfile) (build from source) **or** deploy our published image directly — e.g. on Railway, choose "Deploy from Docker Image" and paste `ghcr.io/mjczone/creuser:0.1.4`. Set `ConnectionStrings__Postgres` / `ConnectionStrings__Redis` env vars. The platform handles HTTPS termination automatically — no Caddy needed. The image honors `$PORT` so it works wherever the platform lands it.

**White-label deployment (rebrand for a customer):**

Keep your branding, plugins, and version pin in a small private repo with a wrapper Dockerfile:

```dockerfile
# your-private-repo/Dockerfile
FROM ghcr.io/mjczone/creuser:0.1.4

# Pre-seed branding (logo, colors, copy) and any custom plugin DLLs
COPY branding/    /data/branding/
COPY plugins/     /data/plugins/
```

Point your platform of choice at *your* repo. Branding and domain logic stay in your repo; you don't fork Creuser. LGPL-3.0 only triggers if you modify Creuser source itself — which the [plugin + branding model](docs/architecture.md) is designed to avoid.

---

## Development

Prerequisites: .NET 10 SDK, Node 24 LTS, Docker.

```bash
git clone https://github.com/mjczone/creuser.git
cd creuser
npm install               # bootstraps .NET tools + SPA + Vitest deps
npm run services:up       # Postgres + Redis (random host ports, auto-wired into appsettings.Development.local.json)
npm run dev               # Quasar dev server + dotnet watch in parallel
```

Open <http://localhost:9000> (Quasar dev server). `/api`, `/hub`, and `/scalar` proxy to the .NET backend on `http://localhost:5128`.

See [docs/architecture.md](docs/architecture.md) for the full developer guide, [`CONTRIBUTING.md`](CONTRIBUTING.md) for contribution conventions.

---

## Documentation & Resources

Coming soon ...

---

## License

This project is licensed under the GNU Lesser General Public License v3.0 or later (LGPL-3.0-or-later) - see the [LICENSE](LICENSE) file for details.

**What this means:**

- ✅ You can use Creuser in commercial applications
- ✅ You can modify and distribute Creuser
- ✅ Your application code remains under your chosen license
- ⚠️ Changes to Creuser itself must be contributed back under LGPL

---

## Support

- 🐛 **Bug Reports** - [GitHub Issues](https://github.com/mjczone/creuser/issues)
- 💬 **Discussions** - [GitHub Discussions](https://github.com/mjczone/creuser/discussions)
- 💻 **Contributing** - See [CONTRIBUTING.md](CONTRIBUTING.md) for current contribution guidelines

---

<div align="center">

**Built with ❤️ by MJCZone Inc.**

[Website](https://mjczone.com) • [GitHub](https://github.com/mjczone) • [NuGet](https://www.nuget.org/profiles/mjczone)

</div>
