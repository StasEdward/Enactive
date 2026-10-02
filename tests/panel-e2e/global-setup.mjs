// One gateway for the whole run, on a database of its own: built, started in Development with the development
// sign-in, and waited for. The teardown stops it and drops the database, whatever the tests did.
import { spawn, spawnSync } from 'node:child_process';
import { createWriteStream, mkdirSync, mkdtempSync } from 'node:fs';
import { createServer } from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { randomBytes } from 'node:crypto';
import {
  repository, configuration, logs, gatewayProject, gatewayDll, harnessProject, database, mysqlServer
} from './fixtures.mjs';

export default async function globalSetup() {
  // CI builds both in a step of its own; a developer's run builds what it is about to start, so a test never
  // runs against yesterday's binaries.
  if (process.env.E2E_NO_BUILD !== '1') {
    for (const project of [gatewayProject, harnessProject]) {
      const built = spawnSync('dotnet', ['build', project, '-c', configuration, '-nologo', '-v', 'q'],
        { cwd: repository, stdio: 'inherit' });
      if (built.status !== 0) throw new Error(`Building ${project} failed.`);
    }
  }

  // Named as the gateway tests name theirs, so the account that may create them can create this one too.
  const name = `enactive_test_e2e_${randomBytes(8).toString('hex')}`;
  database('create', name);

  const port = await freePort();
  const origin = `http://127.0.0.1:${port}`;
  mkdirSync(logs, { recursive: true });
  const log = createWriteStream(path.join(logs, 'gateway.log'));

  // Run from its project folder: that is its content root, where wwwroot is.
  const gateway = spawn('dotnet', [gatewayDll], {
    cwd: gatewayProject,
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: 'Development',
      ASPNETCORE_URLS: origin,
      ENACTIVE_DEV_SIGNIN: 'true',
      ENACTIVE_REMOTE_DB: `${mysqlServer.replace(/;?$/, ';')}Database=${name};`,
      ENACTIVE_DATA: mkdtempSync(path.join(os.tmpdir(), 'enactive-e2e-gateway-'))
    },
    stdio: ['ignore', 'pipe', 'pipe']
  });
  gateway.stdout.pipe(log);
  gateway.stderr.pipe(log);
  let exited = null;
  gateway.on('exit', (code) => { exited = code; });

  const stop = async () => {
    if (exited === null) {
      const gone = new Promise((resolve) => gateway.once('exit', resolve));
      gateway.kill();
      await gone;
    }
    database('drop', name);
  };

  try {
    await healthy(origin, () => exited);
  } catch (error) {
    await stop();
    throw error;
  }

  process.env.E2E_BASE_URL = origin;
  process.env.E2E_DATABASE = name;
  return stop;
}

function freePort() {
  return new Promise((resolve, reject) => {
    const server = createServer();
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const { port } = server.address();
      server.close(() => resolve(port));
    });
  });
}

/** Until /health answers. A gateway that exits first - a migration that failed, say - is said at once. */
async function healthy(origin, exitedWith) {
  const deadline = Date.now() + 60_000;
  while (Date.now() < deadline) {
    if (exitedWith() !== null) {
      throw new Error(`The gateway exited (${exitedWith()}) before it was healthy; see ${path.join(logs, 'gateway.log')}.`);
    }
    try {
      if ((await fetch(`${origin}/health`)).ok) return;
    } catch {
      // Not listening yet.
    }
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
  throw new Error(`The gateway did not answer /health in 60 s; see ${path.join(logs, 'gateway.log')}.`);
}
