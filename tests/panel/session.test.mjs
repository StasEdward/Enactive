import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createGuard, pollOnce, Stale } from '../../src/Enactive.Remote.Gateway/wwwroot/js/session-guard.js';
import { openSessionChannel } from '../../src/Enactive.Remote.Gateway/wwwroot/js/session-channel.js';

// Every channel made by one factory is on the same bus: a message posted on one end reaches the others and
// never the one that posted it, as a BroadcastChannel does.
function fakeBus() {
  const ends = [];
  const posted = [];

  return {
    posted,
    factory: (name) => {
      const end = {
        name,
        closed: false,
        onmessage: null,
        postMessage(data) {
          posted.push(data);
          for (const other of ends) {
            if (other !== end && !other.closed) other.onmessage?.({ data });
          }
        },
        close() { end.closed = true; }
      };
      ends.push(end);
      return end;
    },
    // What any page of the same origin can do: post whatever it likes on the channel by name.
    inject: (data) => {
      for (const end of ends) end.onmessage?.({ data });
    }
  };
}

test('a response from the previous account is dropped', async () => {
  const guard = createGuard();
  let release;
  const gate = new Promise((resolve) => { release = resolve; });
  // Alice's answer, delivered whenever the network gets round to it - here, after Bob has signed in.
  const fakeFetch = async () => { await gate; return { userId: 'alice', runs: ['alice-run'] }; };
  const drawn = [];
  let otherAccount = 0;

  const poll = pollOnce({
    read: async () => {
      const started = guard.generation;
      const snapshot = await fakeFetch();
      guard.check(started);
      return snapshot;
    },
    accountId: 'alice',
    apply: (snapshot) => drawn.push(snapshot),
    otherAccount: () => { otherAccount += 1; }
  });

  guard.bump();
  release();

  await assert.rejects(poll, Stale);
  assert.deepEqual(drawn, []);
  assert.equal(otherAccount, 0);
});

test('a changed signal from another tab makes this tab revalidate', () => {
  const bus = fakeBus();
  let revalidated = 0;
  const here = openSessionChannel({ onChanged: () => { revalidated += 1; }, channelFactory: bus.factory });
  const there = openSessionChannel({ onChanged: () => {}, channelFactory: bus.factory });

  there.announce();
  assert.equal(revalidated, 1);

  // The tab that announces is not told about its own announcement.
  here.announce();
  assert.equal(revalidated, 1);
});

test('a message of another type or with content is ignored', () => {
  const bus = fakeBus();
  const calls = [];
  openSessionChannel({ onChanged: (...args) => calls.push(args), channelFactory: bus.factory });

  bus.inject({ type: 'task', prompt: 'steal this' });
  bus.inject({ type: 'token', token: 'abc' });
  bus.inject({ prompt: 'no type at all' });
  bus.inject('changed');
  bus.inject(null);
  bus.inject(undefined);
  bus.inject(['changed']);
  assert.deepEqual(calls, []);

  // Even a "changed" with something attached is only a hint to ask the gateway: nothing it carries is passed on.
  bus.inject({ type: 'changed', token: 'abc', userId: 'mallory' });
  assert.deepEqual(calls, [[]]);
});

test('announce posts only {type: \'changed\'}', () => {
  const bus = fakeBus();
  const channel = openSessionChannel({ onChanged: () => {}, channelFactory: bus.factory });

  channel.announce();
  channel.announce();

  assert.deepEqual(bus.posted, [{ type: 'changed' }, { type: 'changed' }]);
});

test('the channel is named enactive-session and close stops it listening', () => {
  const names = [];
  const bus = fakeBus();
  let calls = 0;
  const channel = openSessionChannel({
    onChanged: () => { calls += 1; },
    channelFactory: (name) => { names.push(name); return bus.factory(name); }
  });

  assert.deepEqual(names, ['enactive-session']);
  channel.close();
  bus.inject({ type: 'changed' });
  assert.equal(calls, 0);
  // Announcing on a closed channel is a no-op rather than an error: a late sign-out must not throw.
  assert.doesNotThrow(() => channel.announce());
});

test('without BroadcastChannel the channel does nothing and does not throw', () => {
  const saved = globalThis.BroadcastChannel;
  delete globalThis.BroadcastChannel;

  try {
    const channel = openSessionChannel({ onChanged: () => {} });
    assert.doesNotThrow(() => channel.announce());
    assert.doesNotThrow(() => channel.close());
  } finally {
    if (saved) globalThis.BroadcastChannel = saved;
  }
});

test('a factory that throws leaves a channel that does nothing', () => {
  const channel = openSessionChannel({
    onChanged: () => {},
    channelFactory: () => { throw new Error('SecurityError'); }
  });

  assert.doesNotThrow(() => channel.announce());
  assert.doesNotThrow(() => channel.close());
});
