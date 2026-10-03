import { test } from 'node:test';
import assert from 'node:assert/strict';
import { singleFlight } from '../../src/Enactive.Remote.Gateway/wwwroot/js/single-flight.js';

// Work the test finishes by hand, so two calls can be made while it is running.
function heldWork() {
  let runs = 0;
  let finish;
  const work = () => {
    runs += 1;
    return new Promise((resolve) => { finish = resolve; });
  };
  return { work, runs: () => runs, finish: (value) => finish(value) };
}

test('two overlapping calls run the work once and both get its result', async () => {
  const held = heldWork();
  const once = singleFlight(held.work);

  const first = once();
  const second = once();
  held.finish('snapshot');

  assert.deepEqual(await Promise.all([first, second]), ['snapshot', 'snapshot']);
  assert.equal(held.runs(), 1);
});

test('a call after the work has finished runs it again', async () => {
  const held = heldWork();
  const once = singleFlight(held.work);

  const first = once();
  held.finish(1);
  await first;

  const second = once();
  held.finish(2);

  assert.equal(await second, 2);
  assert.equal(held.runs(), 2);
});

test('a failure is shared too, and does not stop the next call', async () => {
  let runs = 0;
  const once = singleFlight(async () => {
    runs += 1;
    if (runs === 1) throw new Error('unreachable');
    return 'ok';
  });

  await assert.rejects(Promise.all([once(), once()]), /unreachable/);
  assert.equal(await once(), 'ok');
  assert.equal(runs, 2);
});

test('a call under another key does not share the running work', async () => {
  let key = 1;
  const held = heldWork();
  const once = singleFlight(held.work, () => key);

  const first = once();
  key = 2;
  const second = once();

  assert.equal(held.runs(), 2);
  held.finish('second');
  assert.equal(await second, 'second');
  assert.equal(once.pending, null);
  void first; // Left running: the earlier key's work is nobody's business once the key has moved on.
});

test('pending is the running work, and null when nothing runs', async () => {
  const held = heldWork();
  const once = singleFlight(held.work);

  assert.equal(once.pending, null);
  const running = once();
  assert.equal(once.pending, running);
  held.finish();
  await running;
  assert.equal(once.pending, null);
});
