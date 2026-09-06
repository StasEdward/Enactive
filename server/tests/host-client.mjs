// Minimal SignalR JSON client for local protocol tests. Production Host should use
// Microsoft.AspNetCore.SignalR.Client with reconnect and a durable local inbox/outbox.
import net from 'node:net';
import { randomBytes, createHash } from 'node:crypto';

export async function connectHost(base, token) {
  const response = await fetch(`${base}/hubs/host/negotiate?negotiateVersion=1`, { method: 'POST', headers: { Authorization: `Bearer ${token}` } });
  if (!response.ok) throw new Error(`Host negotiate: ${response.status}`);
  const negotiation = await response.json(), url = new URL(base);
  if (url.protocol !== 'http:' || !['127.0.0.1', 'localhost'].includes(url.hostname)) throw new Error('Test client only supports local HTTP.');
  const socket = net.connect(Number(url.port), url.hostname);
  await new Promise((resolve, reject) => { socket.once('connect', resolve); socket.once('error', reject); });
  const key = randomBytes(16).toString('base64');
  const pending = new Map(); let invocation = 0, buffer = Buffer.alloc(0), upgraded = false, handshake;
  const ready = new Promise((resolve, reject) => { handshake = { resolve, reject }; });
  function sendFrame(payload, opcode = 1) {
    const bytes = Buffer.from(payload), mask = randomBytes(4);
    const header = Buffer.alloc(bytes.length < 126 ? 2 : 4);
    header[0] = 0x80 | opcode;
    if (bytes.length < 126) header[1] = 0x80 | bytes.length;
    else { header[1] = 0xfe; header.writeUInt16BE(bytes.length, 2); }
    const masked = Buffer.from(bytes); for (let i = 0; i < masked.length; i++) masked[i] ^= mask[i % 4];
    socket.write(Buffer.concat([header, mask, masked]));
  }
  function message(data) {
    for (const part of data.split('\x1e').filter(Boolean)) {
      const item = JSON.parse(part);
      if (!item.type) { item.error ? handshake.reject(new Error(item.error)) : handshake.resolve(); }
      if (item.type === 3) { const wait = pending.get(item.invocationId); if (wait) { clearTimeout(wait.timer); pending.delete(item.invocationId); item.error ? wait.reject(new Error(item.error)) : wait.resolve(item.result); } }
    }
  }
  socket.on('data', chunk => {
    buffer = Buffer.concat([buffer, chunk]);
    if (!upgraded) {
      const end = buffer.indexOf('\r\n\r\n'); if (end < 0) return;
      const headers = buffer.subarray(0, end).toString();
      const expected = createHash('sha1').update(key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
      if (!headers.startsWith('HTTP/1.1 101') || !headers.includes(expected)) { handshake.reject(new Error('WebSocket upgrade rejected')); socket.destroy(); return; }
      upgraded = true; buffer = buffer.subarray(end + 4); sendFrame('{"protocol":"json","version":1}\x1e');
    }
    while (buffer.length >= 2) {
      const opcode = buffer[0] & 15; let length = buffer[1] & 127, offset = 2;
      if (length === 126) { if (buffer.length < 4) return; length = buffer.readUInt16BE(2); offset = 4; }
      if (length === 127) { if (buffer.length < 10) return; length = Number(buffer.readBigUInt64BE(2)); offset = 10; }
      if (buffer.length < offset + length) return;
      const data = buffer.subarray(offset, offset + length); buffer = buffer.subarray(offset + length);
      if (opcode === 1) message(data.toString());
      if (opcode === 9) sendFrame(data, 10);
      if (opcode === 8) socket.destroy();
    }
  });
  socket.on('error', error => handshake.reject(error));
  socket.on('close', () => { for (const wait of pending.values()) { clearTimeout(wait.timer); wait.reject(new Error('Host disconnected')); } pending.clear(); });
  socket.write(`GET /hubs/host?id=${encodeURIComponent(negotiation.connectionToken)} HTTP/1.1\r\nHost: ${url.host}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: ${key}\r\nSec-WebSocket-Version: 13\r\nAuthorization: Bearer ${token}\r\n\r\n`);
  await Promise.race([ready, new Promise((_, reject) => { const timer = setTimeout(() => reject(new Error('Handshake timeout')), 5000); timer.unref(); })]);
  return { close: () => socket.destroy(), invoke: (target, ...args) => new Promise((resolve, reject) => {
    const id = String(++invocation);
    const timer = setTimeout(() => { pending.delete(id); reject(new Error('Invocation timeout')); }, 5000);
    pending.set(id, { resolve, reject, timer });
    sendFrame(JSON.stringify({ type: 1, invocationId: id, target, arguments: args }) + '\x1e');
  }) };
}
