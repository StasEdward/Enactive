// One service for the whole run (tests/ServerHarness: the real pipeline over a scripted engine), on a data folder of
// its own: built, started, waited for. The teardown stops it and deletes the folder, whatever the tests did.
import { spawn, spawnSync } from 'node:child_process';
import { createWriteStream, mkdirSync, mkdtempSync, rmSync } from 'node:fs';
import { createServer } from 'node:net';
import { createInterface } from 'node:readline';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const configuration = process.env.E2E_CONFIGURATION ?? 'Debug';
const harnessProject = path.join(repository, 'tests', 'ServerHarness');
export const harnessBin = path.join(harnessProject, 'bin', configuration, 'net10.0');
const logs = path.join(repository, 'tests', 'server-e2e', 'test-results', 'processes');

export default async function globalSetup() {
  // A developer's run builds what it is about to start, so a test never runs against yesterday's binaries.
  if (process.env.E2E_NO_BUILD !== '1') {
    const built = spawnSync('dotnet', ['build', harnessProject, '-c', configuration, '-nologo', '-v', 'q'],
      { cwd: repository, stdio: 'inherit' });
    if (built.status !== 0) throw new Error('Building tests/ServerHarness failed.');
  }

  const data = mkdtempSync(path.join(os.tmpdir(), 'enactive-server-e2e-'));
  const port = await freePort();
  mkdirSync(logs, { recursive: true });
  const log = createWriteStream(path.join(logs, 'server.log'));

  const service = spawn('dotnet', [path.join(harnessBin, 'ServerHarness.dll'), '--data', data, '--port', String(port)],
    { cwd: harnessBin, stdio: ['ignore', 'pipe', 'pipe'] });
  service.stderr.pipe(log);
  let exited = null;
  service.on('exit', (code) => { exited = code; });

  const ready = new Promise((resolve, reject) => {
    const lines = createInterface({ input: service.stdout });
    lines.on('line', (line) => {
      log.write(line + '\n');
      if (line.trim() === 'ready') resolve();
    });
    service.once('exit', (code) => reject(new Error(`The service exited (${code}) before it was ready; see ${logs}.`)));
    setTimeout(() => reject(new Error(`The service was not ready in 60 s; see ${logs}.`)), 60_000);
  });

  const stop = async () => {
    if (exited === null) {
      const gone = new Promise((resolve) => service.once('exit', resolve));
      service.kill();
      await gone;
    }
    rmSync(data, { recursive: true, force: true });
  };

  try {
    await ready;
  } catch (error) {
    await stop();
    throw error;
  }

  process.env.E2E_BASE_URL = `https://localhost:${port}`;
  process.env.E2E_DATA = data;
  process.env.E2E_SERVER_DLL = path.join(harnessBin, 'Enactive.Server.dll');
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
