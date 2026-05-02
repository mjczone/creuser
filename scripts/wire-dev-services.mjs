#!/usr/bin/env node
//
// Reads the random host ports Docker assigned to the dev Postgres and Redis
// containers and writes connection strings into
// src/Creuser.Web/appsettings.Development.local.json (gitignored).
//
// The .NET app loads that file at startup (see Program.cs:
// AddJsonFile($"appsettings.{env}.local.json", optional: true)), so as soon
// as services are up the backend can talk to them without anyone editing
// config by hand.
//
// Run via `npm run services:up` (chains start → wire) or `npm run services:wire`.

import { execSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = resolve(fileURLToPath(import.meta.url), '..', '..');

function getHostPort(container, containerPort) {
  let raw;
  try {
    raw = execSync(`docker port ${container} ${containerPort}`, { encoding: 'utf8' });
  } catch (e) {
    throw new Error(
      `Could not read port for ${container}:${containerPort}. ` +
        `Is the container running? Try \`npm run services:up\` first. (${e.message.trim()})`,
    );
  }
  // Output is one line per binding, e.g. "0.0.0.0:49158" or "127.0.0.1:49158".
  // We want the IPv4 host port. Take the first one.
  const match = raw.split('\n').find((l) => l.trim().length > 0)?.match(/:(\d+)\s*$/);
  if (!match) {
    throw new Error(`Could not parse host port from \`docker port\` output: ${JSON.stringify(raw)}`);
  }
  return Number(match[1]);
}

const postgresPort = getHostPort('creuser-dev-postgres', 5432);
const redisPort = getHostPort('creuser-dev-redis', 6379);

const config = {
  ConnectionStrings: {
    Postgres: `Host=localhost;Port=${postgresPort};Database=creuser;Username=creuser;Password=creuser_dev;Include Error Detail=true`,
    Redis: `localhost:${redisPort}`,
  },
};

const outPath = resolve(repoRoot, 'src/Creuser.Web/appsettings.Development.local.json');
mkdirSync(dirname(outPath), { recursive: true });
writeFileSync(outPath, JSON.stringify(config, null, 2) + '\n');

console.log(
  `wired dev services: Postgres → :${postgresPort}, Redis → :${redisPort}\n  → ${outPath}`,
);
