import { test } from 'node:test';
import assert from 'node:assert/strict';
import { pollOnce } from '../../src/Enactive.Remote.Gateway/wwwroot/js/session-guard.js';
import { get, abandonRequests, Stale } from '../../src/Enactive.Remote.Gateway/wwwroot/js/api.js';
import { openSessionChannel, onSessionSignal, removedViewAction } from '../../src/Enactive.Remote.Gateway/wwwroot/js/session-channel.js';

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

// A fetch whose answer the test releases by hand. It ignores the abort signal on purpose: an answer that
// arrives anyway is the case the generation exists for.
function heldFetch(body) {
  let release;
  const gate = new Promise((resolve) => { release = resolve; });
  globalThis.fetch = async () => {
    await gate;
    return new Response(JSON.stringify(body), { status: 200 });
  };
  return release;
}

test('a response from the previous account is dropped', async () => {
  // Alice's poll, answered after an account change (abandonRequests is the bump a reset makes). Her snapshot names
  // her, and the page is still holding her id, so only the generation stands between it and Bob's screen.
  const release = heldFetch({ userId: 'alice', runs: ['alice-run'] });
  const drawn = [];
  let otherAccount = 0;

  const poll = pollOnce({
    read: () => get('/api/state'),
    accountId: 'alice',
    apply: (snapshot) => drawn.push(snapshot),
    otherAccount: () => { otherAccount += 1; }
  });

  abandonRequests();
  release();

  await assert.rejects(poll, Stale);
  assert.deepEqual(drawn, []);
  assert.equal(otherAccount, 0);
});

test('the same response is drawn when the account did not change', async () => {
  const release = heldFetch({ userId: 'alice', runs: ['alice-run'] });
  const drawn = [];

  const poll = pollOnce({
    read: () => get('/api/state'),
    accountId: 'alice',
    apply: (snapshot) => drawn.push(snapshot),
    otherAccount: () => assert.fail('the account did not change')
  });

  release();

  assert.equal(await poll, true);
  assert.deepEqual(drawn, [{ userId: 'alice', runs: ['alice-run'] }]);
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

function signal(overrides) {
  const calls = [];
  const effects = {
    boot: () => calls.push('boot'),
    revalidate: () => calls.push('revalidate'),
    toSignIn: () => calls.push('toSignIn')
  };
  onSessionSignal({ account: null, view: 'other', ...effects, ...overrides });
  return calls;
}

test('a signal revalidates a tab that has an account', () => {
  assert.deepEqual(signal({ account: { id: 'alice' } }), ['revalidate']);
});

test('a signal makes a tab at sign-in boot, so it follows a sign-in made in another tab', () => {
  assert.deepEqual(signal({ account: null, view: 'other' }), ['boot']);
});

test('a signal leaves a tab at "device removed" out of the panel', () => {
  // Only the session check: it moves to sign-in when there is no session and does nothing when there is one.
  assert.deepEqual(signal({ account: null, view: 'removed' }), ['toSignIn']);
});

test('a changed signal over the channel reaches each branch', () => {
  const bus = fakeBus();
  const calls = [];
  let account = null;
  let view = 'other';
  openSessionChannel({
    onChanged: () => onSessionSignal({
      account, view,
      boot: () => calls.push('boot'),
      revalidate: () => calls.push('revalidate'),
      toSignIn: () => calls.push('toSignIn')
    }),
    channelFactory: bus.factory
  });
  const other = openSessionChannel({ onChanged: () => {}, channelFactory: bus.factory });

  other.announce();
  view = 'removed';
  other.announce();
  account = { id: 'bob' };
  other.announce();

  assert.deepEqual(calls, ['boot', 'toSignIn', 'revalidate']);
});

test('a signal at "device removed" leaves for sign-in when nobody is signed in', () => {
  assert.equal(removedViewAction({ authenticated: false }, 'alice'), 'sign-in');
});

test('a signal at "device removed" boots when another account is signed in', () => {
  assert.equal(removedViewAction({ authenticated: true, user: { id: 'bob' } }, 'alice'), 'boot');
});

test('a signal at "device removed" stays put when the removed account is the one signed in', () => {
  // Its session is still open on purpose: entering the panel would use the removed device's keys again.
  assert.equal(removedViewAction({ authenticated: true, user: { id: 'alice' } }, 'alice'), 'stay');
});

test('a view that does not know its account is never taken into the panel', () => {
  assert.equal(removedViewAction({ authenticated: true, user: { id: 'bob' } }, null), 'stay');
});
