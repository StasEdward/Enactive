// Process control for the browser tests: the gateway (started once, in global-setup.mjs), the computers
// (tests/RemoteHostHarness, one process per computer) and the database (made, dumped and dropped through the
// harness, because Node has no MySQL client).
import { spawn, spawnSync } from 'node:child_process';
import { createInterface } from 'node:readline';
import { createWriteStream, mkdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');

/** Debug locally, Release on CI (the gateway job builds Release before this runs). */
export const configuration = process.env.E2E_CONFIGURATION ?? 'Debug';

/** Where the processes' own output is kept, for whoever reads a failure. */
export const logs = path.join(repository, 'tests', 'panel-e2e', 'test-results', 'processes');

export const gatewayProject = path.join(repository, 'src', 'Enactive.Remote.Gateway');
export const gatewayDll = path.join(gatewayProject, 'bin', configuration, 'net10.0', 'Enactive.Remote.Gateway.dll');
export const harnessProject = path.join(repository, 'tests', 'RemoteHostHarness');
export const harnessDll = path.join(harnessProject, 'bin', configuration, 'net10.0', 'RemoteHostHarness.dll');

/**
 * The MySQL server the tests may make a database on, as a connection string with no database. The developer's
 * Docker MySQL (compose.yaml) unless the environment names another - CI's service listens on 3306.
 */
export const mysqlServer = process.env.ENACTIVE_E2E_MYSQL
  ?? 'Server=127.0.0.1;Port=13306;User ID=root;Password=enactive-dev;';

/** Runs the harness's database command and returns its stdout; throws with its stderr when it fails. */
export function database(verb, name) {
  const result = spawnSync('dotnet', [harnessDll, 'db', verb, name], {
    env: { ...process.env, ENACTIVE_E2E_MYSQL: mysqlServer },
    encoding: 'utf8',
    maxBuffer: 64 * 1024 * 1024
  });
  if (result.status !== 0) {
    throw new Error(`db ${verb} ${name} failed (${result.status}): ${result.stderr || result.error}`);
  }
  return result.stdout;
}

/** Every row of every table of the run's database, as {table: [{column: text}]}. */
export function dumpDatabase() {
  return JSON.parse(database('dump', process.env.E2E_DATABASE));
}

/**
 * One computer: the harness, started now and paired with `pair(code)`. Its stdout lines are kept so a test can
 * wait for one that was said before it started waiting.
 */
export function startComputer(name, { workspace }) {
  mkdirSync(logs, { recursive: true });
  const log = createWriteStream(path.join(logs, `computer-${name}.log`));
  const child = spawn('dotnet', [harnessDll, '--workspace', workspace], { stdio: ['pipe', 'pipe', 'pipe'] });
  const lines = [];
  const waiters = new Set();
  let exited = null;

  createInterface({ input: child.stdout }).on('line', (line) => {
    log.write(`${line}\n`);
    lines.push(line);
    for (const waiter of [...waiters]) waiter.check();
  });
  child.stderr.on('data', (chunk) => log.write(chunk));
  child.on('exit', (code) => {
    exited = code;
    log.write(`exited ${code}\n`);
    for (const waiter of [...waiters]) waiter.check();
  });

  return {
    lines,

    pair(code) {
      child.stdin.write(`${code}\n`);
    },

    /** The first line matching `pattern` (from the start of the output), as its match. */
    waitFor(pattern, timeout = 30_000) {
      return new Promise((resolve, reject) => {
        const waiter = {
          check() {
            const found = lines.map((line) => line.match(pattern)).find(Boolean);
            if (found) {
              done();
              resolve(found);
            } else if (exited !== null) {
              done();
              reject(new Error(`Computer ${name} exited (${exited}) before saying ${pattern}; it said: ${lines.join(' | ')}`));
            }
          }
        };
        const timer = setTimeout(() => {
          done();
          reject(new Error(`Computer ${name} did not say ${pattern} in ${timeout} ms; it said: ${lines.join(' | ')}`));
        }, timeout);
        const done = () => {
          clearTimeout(timer);
          waiters.delete(waiter);
        };
        waiters.add(waiter);
        waiter.check();
      });
    },

    /** Ends it the way the harness expects - its stdin closes - and kills it if it does not go. */
    async stop() {
      if (exited !== null) return;
      const gone = new Promise((resolve) => child.once('exit', resolve));
      child.stdin.end();
      const timer = setTimeout(() => child.kill(), 5_000);
      await gone;
      clearTimeout(timer);
      log.end();
    }
  };
}

/** The connection code's members: `p` is the pairing secret, `h` the computer's id. */
export function readConnectionCode(code) {
  const prefix = 'enactive-connect:';
  if (!code.startsWith(prefix)) throw new Error(`Not a connection code: ${code.slice(0, 30)}`);
  return JSON.parse(Buffer.from(code.slice(prefix.length), 'base64url').toString('utf8'));
}

/** The invitation link's fragment members: `i` the invitation, `p` the pairing secret. */
export function readInviteLink(link) {
  return Object.fromEntries(new URL(link).hash.slice(1).split('&').map((part) => part.split('=')));
}

/** Text no other part of a run could produce by chance: what the tests look for where it must not be. */
export function marker(what) {
  return `${what}-${Math.random().toString(36).slice(2, 10)}`;
}
