import { test } from 'node:test';
import assert from 'node:assert/strict';
import { randomBytes, randomUUID } from 'node:crypto';
import { spawn } from 'node:child_process';
import { mkdtemp, mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import net from 'node:net';
import { connectHost } from './host-client.mjs';

test('gateway: authentication, durable commands, approval lifecycle, revocation and restart', async t => {
  const root = fileURLToPath(new URL('../', import.meta.url));
  await mkdir(new URL('../test-results/', import.meta.url), { recursive: true });
  const data = await mkdtemp(fileURLToPath(new URL('../test-results/gateway-', import.meta.url)));
  const listener = net.createServer(); await new Promise(r => listener.listen(0, '127.0.0.1', r));
  const port = listener.address().port; await new Promise(r => listener.close(r));
  const base = `http://127.0.0.1:${port}`, key = randomBytes(32).toString('hex');
  let process, log = '', csrf, host, secondHost;
  const cookies = new Map();
  async function boot() {
    process = spawn('dotnet', ['bin/Debug/net10.0/Enactive.Server.dll', '--urls', base], { cwd: root, windowsHide: true,
      env: { ...globalThis.process.env, ASPNETCORE_ENVIRONMENT: 'Development', ENACTIVE_OWNER_KEY: key, ENACTIVE_DATA: data } });
    process.stdout.on('data', b => log += b); process.stderr.on('data', b => log += b);
    for (let i = 0; i < 100; i++) {
      if (process.exitCode !== null) throw new Error(log);
      try { if ((await fetch(`${base}/health`)).ok) return; } catch {}
      await new Promise(r => setTimeout(r, 100));
    }
    throw new Error(log);
  }
  async function stop() {
    if (!process || process.exitCode !== null) return;
    await new Promise(resolve => { process.once('exit', resolve); process.kill(); });
  }
  t.after(async () => { host?.close(); secondHost?.close(); await stop(); if (!t.passed) console.log(log); });
  async function request(path, body, expected = 200, includeCsrf = true) {
    const response = await fetch(base + '/api' + path, { method: body === undefined ? 'GET' : 'POST',
      headers: { 'Content-Type': 'application/json', Cookie: [...cookies].map(([k, v]) => `${k}=${v}`).join('; '), ...(includeCsrf ? { 'X-CSRF-TOKEN': csrf || '' } : {}) },
      body: body === undefined ? undefined : JSON.stringify(body) });
    for (const cookie of response.headers.getSetCookie()) { const pair = cookie.split(';')[0], index = pair.indexOf('='); cookies.set(pair.slice(0, index), pair.slice(index + 1)); }
    const text = await response.text(); assert.equal(response.status, expected, `${path}: ${text}`);
    return text ? JSON.parse(text) : null;
  }
  await boot();
  await t.test('owner API rejects anonymous, wrong credentials and missing CSRF', async () => {
    await request('/state', undefined, 401);
    csrf = (await request('/session')).csrfToken;
    await request('/login', { key }, 400, false);
    await request('/login', { key: 'wrong' }, 401);
    await request('/login', { key }); csrf = (await request('/session')).csrfToken;
    await request('/hosts', { name: 'Should not exist' }, 400, false);
  });
  const device = await request('/hosts', { name: 'Integration Host' });
  const other = await request('/hosts', { name: 'Other Host' });
  host = await connectHost(base, device.token); secondHost = await connectHost(base, other.token);
  await host.invoke('Sync', [{ id: 'workspace-1', name: 'Enactive' }]);
  const task = await request('/tasks', { hostId: device.id, workspaceId: 'workspace-1', title: 'Test remote loop', prompt: 'Run tests; ask before the tool call.' });
  const startId = randomUUID(); let command, runId;
  await t.test('workspace validation, persisted start, deduplication and host isolation', async () => {
    await request('/tasks', { hostId: device.id, workspaceId: 'arbitrary-path', title: 'Bad', prompt: 'Bad' }, 400);
    command = await request(`/tasks/${task.id}/start`, { commandId: startId }); runId = JSON.parse(command.payload).runId;
    assert.equal((await request(`/tasks/${task.id}/start`, { commandId: startId })).id, command.id);
    await request(`/runs/${runId}/cancel`, { commandId: startId }, 409);
    await request(`/tasks/${task.id}/start`, { commandId: randomUUID() }, 409);
    assert.equal((await host.invoke('Sync', [{ id: 'workspace-1', name: 'Enactive' }])).length, 1);
    await assert.rejects(secondHost.invoke('Acknowledge', startId), /not found/i);
    await assert.rejects(secondHost.invoke('Publish', { eventId: randomUUID(), runId, kind: 'Running' }), /not found/i);
    await host.invoke('Acknowledge', startId); await host.invoke('Acknowledge', startId);
    assert.equal((await host.invoke('Sync', [{ id: 'workspace-1', name: 'Enactive' }])).length, 0);
  });
  const approvalId = randomUUID();
  await t.test('full approval is saved, one answer wins, Host confirms outcome', async () => {
    await host.invoke('Publish', { eventId: randomUUID(), runId, kind: 'Running' });
    const ev = { eventId: randomUUID(), runId, kind: 'ApprovalRequested', approvalId, toolCallId: 'call-1', tool: 'run_command', arguments: 'dotnet test\n# complete command tail', workingDirectory: 'C:/work/Enactive', detail: 'Tests need permission', actionHash: 'hash-of-exact-call' };
    await host.invoke('Publish', ev); await host.invoke('Publish', ev);
    let snapshot = await request('/state'); assert.equal(snapshot.approvals.length, 1); assert.equal(snapshot.notices.length, 1);
    assert.equal(snapshot.approvals[0].arguments, ev.arguments);
    await request(`/approvals/${approvalId}/resolve`, { commandId: randomUUID(), decision: 'allow', actionHash: 'changed' }, 409);
    const decision = { commandId: randomUUID(), decision: 'allow', actionHash: ev.actionHash };
    await request(`/approvals/${approvalId}/resolve`, decision); await request(`/approvals/${approvalId}/resolve`, decision);
    await request(`/approvals/${approvalId}/resolve`, { ...decision, commandId: randomUUID(), decision: 'deny' }, 409);
    snapshot = await request('/state'); assert.equal(snapshot.approvals[0].status, 'DecisionQueued'); assert.equal(snapshot.runs[0].status, 'WaitingForUser');
    await host.invoke('Publish', { eventId: randomUUID(), runId, kind: 'ApprovalResolved', approvalId, actionHash: ev.actionHash, detail: 'Allowed' });
    await host.invoke('Publish', { eventId: randomUUID(), runId, kind: 'Completed', detail: 'All tests passed.' });
    snapshot = await request('/state'); assert.equal(snapshot.runs[0].status, 'Completed'); assert.equal(snapshot.notices.length, 2);
    await assert.rejects(host.invoke('Publish', { eventId: randomUUID(), runId, kind: 'Running' }), /ended/i);
  });
  await t.test('restart preserves state and owner session', async () => {
    host.close(); secondHost.close(); await stop(); await boot();
    const snapshot = await request('/state'); assert.equal(snapshot.tasks.length, 1); assert.equal(snapshot.runs[0].summary, 'All tests passed.');
    assert.equal(snapshot.approvals[0].status, 'Allowed');
    assert.ok(!JSON.stringify(snapshot).includes(device.token)); assert.ok(!JSON.stringify(snapshot).includes('tokenHash'));
    host = await connectHost(base, device.token);
  });
  await t.test('cancel waits for Host and revocation rejects authentication', async () => {
    const next = await request(`/tasks/${task.id}/start`, { commandId: randomUUID() }); const nextId = JSON.parse(next.payload).runId;
    await request(`/runs/${nextId}/cancel`, { commandId: randomUUID() });
    assert.equal((await request('/state')).runs.at(-1).status, 'CancelRequested');
    await host.invoke('Publish', { eventId: randomUUID(), runId: nextId, kind: 'Cancelled', detail: 'Stopped before execution.' });
    await request(`/hosts/${device.id}/revoke`, {});
    await assert.rejects(connectHost(base, device.token), /401/);
    await request(`/tasks/${task.id}/start`, { commandId: randomUUID() }, 404);
    await request('/notices/read', {}); assert.ok((await request('/state')).notices.every(n => n.read));
  });
});
