// The panel's keys for one account in this browser: the device key, the computers' host keys, the
// computers' pinned signing keys and the pairing secrets still waiting for a grant. The logic lives here,
// over a small adapter, so `node --test` runs it against memory; the IndexedDB adapter below only moves
// records in and out. Nothing here goes to localStorage: anything a script on the page can read as text
// is one cross-site-scripting bug away from leaving the browser, and localStorage is shared by every
// account signed in to this profile.
import { equal } from './bytes.js';

// The gateway's user ids are 32 hex characters; letters, digits and hyphens of any case are let through so a
// GUID with dashes also fits. The id becomes part of a database name, so anything else is refused before it
// is used: a missing id would otherwise open "enactive-keys-undefined", one store shared by every account
// whose snapshot lacked it, and an id that is not the gateway's shape did not come from the gateway.
const USER_ID = /^[A-Za-z0-9-]{1,64}$/;
const DATABASE_PREFIX = 'enactive-keys-';
const STORES = Object.freeze(['device', 'hostKeys', 'hostSigning', 'pending']);
// The device store holds one record, under this key.
const DEVICE = 'self';
const ECDH = { name: 'ECDH', namedCurve: 'P-256' };
const HOST_KEY = 32;

/** A computer's signing key already pinned in this store is not the one offered now. */
export class PinMismatchError extends Error {
  constructor(hostId) {
    super(`Computer ${hostId} already has another signing key pinned.`);
    this.name = 'PinMismatchError';
  }
}

/** The store was closed, or forgotten: its own error, so a caller can tell it from a failure to read. */
export class KeystoreClosedError extends Error {
  constructor() {
    super('This key store is closed.');
    this.name = 'KeystoreClosedError';
  }
}

/** The database could not be deleted because another tab of this site keeps it open. */
export class BlockedError extends Error {
  constructor() {
    super('Close the other tabs of this site and try again.');
    this.name = 'BlockedError';
  }
}

// How long a deletion another connection blocks is waited for. The browser holds the request, without a word,
// until every other connection closes: a tab still running a page from before onversionchange closed its
// connection kept "Forget this device" spinning for as long as that tab stayed open.
export const BLOCKED_AFTER_MS = 5000;

/**
 * Opens the key store of one account. One database per account (`enactive-keys-<userId>`): two accounts
 * signed in one after the other in one browser profile never see each other's keys, because neither ever
 * opens the other's database. `now` is the clock pending secrets expire by; a test passes its own.
 */
export async function openKeystore(userId, adapter = indexedDbAdapter, now = () => Date.now()) {
  if (typeof userId !== 'string' || !USER_ID.test(userId)) {
    throw new TypeError('A user id is 1 to 64 letters, digits or hyphens.');
  }
  const name = DATABASE_PREFIX + userId;
  const db = await adapter.open(name, STORES);
  let closed = false;

  // A store used after forget() would write into a database that was just deleted, or recreate it.
  const live = () => {
    if (closed) throw new KeystoreClosedError();
    return db;
  };

  const hostKeyEntries = async (hostId) =>
    (await live().entries('hostKeys')).filter(([key]) => key[0] === hostId);

  const store = {
    /** The device record, or null before createDevice(). `id` is null until the gateway has registered it. */
    async device() {
      const record = await live().get('device', DEVICE);
      return record ? { id: record.id ?? null, privateKey: record.privateKey, publicRaw: record.publicRaw } : null;
    },

    /**
     * Makes the device key. The private key is generated non-extractable and kept as a CryptoKey, which
     * IndexedDB stores without its bytes ever reaching script: a script injected into the page can use the
     * key while the page is open, but cannot carry it away to use from elsewhere. If a device exists already
     * (another tab made one first) that one is kept and returned: replacing it would orphan the public key
     * the gateway registered and every grant made to it.
     */
    async createDevice() {
      const pair = await crypto.subtle.generateKey(ECDH, false, ['deriveBits']);
      const publicRaw = new Uint8Array(await crypto.subtle.exportKey('raw', pair.publicKey));
      if (await live().add('device', DEVICE, { id: null, privateKey: pair.privateKey, publicRaw })) {
        return { privateKey: pair.privateKey, publicRaw };
      }
      const existing = await live().get('device', DEVICE);
      return { privateKey: existing.privateKey, publicRaw: existing.publicRaw };
    },

    async setDeviceId(id) {
      requireText(id, 'A device id');
      const record = await live().get('device', DEVICE);
      if (!record) throw new Error('There is no device key to name.');
      await live().put('device', DEVICE, { ...record, id });
    },

    /** Every epoch key held for a computer, as a Map from epoch to its 32 raw bytes, oldest first. */
    async hostKeys(hostId) {
      requireText(hostId, 'A computer id');
      const entries = await hostKeyEntries(hostId);
      entries.sort(([a], [b]) => a[1] - b[1]);
      return new Map(entries.map(([key, value]) => [key[1], value]));
    },

    /**
     * Keeps a computer's key for one epoch. Raw bytes, not a non-extractable CryptoKey: a trusted device
     * admits another by wrapping these bytes for it, which a key script cannot read could not be. The
     * record is keyed [hostId, epoch]. An epoch already held keeps its first key and this returns false:
     * a second, different key for one epoch would replace the key everything of that epoch was sealed
     * with, which is a rollback, not news. The add is one atomic insert, so two tabs storing the same grant
     * at once still leave one key.
     */
    async addHostKey(hostId, epoch, keyBytes) {
      requireText(hostId, 'A computer id');
      if (!Number.isSafeInteger(epoch) || epoch < 1) throw new TypeError('An epoch is a whole number from 1.');
      if (!(keyBytes instanceof Uint8Array) || keyBytes.length !== HOST_KEY) {
        throw new TypeError('A host key is 32 bytes.');
      }
      return live().add('hostKeys', [hostId, epoch], keyBytes.slice());
    },

    async newestEpoch(hostId) {
      requireText(hostId, 'A computer id');
      const epochs = (await hostKeyEntries(hostId)).map(([key]) => key[1]);
      return epochs.length === 0 ? null : Math.max(...epochs);
    },

    /** The signing public key pinned for a computer (65 raw bytes), or null if none is. */
    async hostSigningKey(hostId) {
      requireText(hostId, 'A computer id');
      return (await live().get('hostSigning', hostId)) ?? null;
    },

    /**
     * Pins a computer's signing public key at its first verified grant. The same key again is a no-op; a
     * different one throws PinMismatchError and the first stays. Pinning anew would let whoever produced
     * the second key - a gateway that swapped it, or a revoked device - sign every later rotation grant.
     */
    async pinHostSigningKey(hostId, raw) {
      requireText(hostId, 'A computer id');
      if (!(raw instanceof Uint8Array) || raw.length !== 65 || raw[0] !== 4) {
        throw new TypeError('A signing public key is 65 bytes beginning with 0x04.');
      }
      if (await live().add('hostSigning', hostId, raw.slice())) return;
      const pinned = await live().get('hostSigning', hostId);
      if (!equal(pinned, raw)) throw new PinMismatchError(hostId);
    },

    /** Every computer this store holds a key or a pin for. */
    async hosts() {
      const fromKeys = (await live().entries('hostKeys')).map(([key]) => key[0]);
      const fromPins = (await live().entries('hostSigning')).map(([key]) => key);
      return [...new Set([...fromKeys, ...fromPins])].sort();
    },

    /** Forgets a computer that was removed: its keys and its pin, so a new computer is never judged by the old one's key. */
    async dropHost(hostId) {
      requireText(hostId, 'A computer id');
      for (const [key] of await hostKeyEntries(hostId)) await live().delete('hostKeys', key);
      await live().delete('hostSigning', hostId);
    },

    /** A pairing secret waiting for its grant, or null. One past its expiry is deleted, not only hidden. */
    async pending(id) {
      requireText(id, 'A pairing id');
      const record = await live().get('pending', id);
      if (!record) return null;
      if (now() >= record.expiresAt) {
        await live().delete('pending', id);
        return null;
      }
      return record.secret;
    },

    async setPending(id, secret, expiresAt) {
      requireText(id, 'A pairing id');
      if (!(secret instanceof Uint8Array)) throw new TypeError('A pairing secret is bytes.');
      if (!Number.isFinite(expiresAt)) throw new TypeError('An expiry is a time in milliseconds.');
      await live().put('pending', id, { secret: secret.slice(), expiresAt });
    },

    async dropPending(id) {
      requireText(id, 'A pairing id');
      await live().delete('pending', id);
    },

    /** Deletes this account's whole database. The store cannot be used afterwards. */
    async forget() {
      live();
      closed = true;
      // An open connection would hold the deletion until it closed; this one is closed first.
      db.close();
      await adapter.deleteDatabase(name);
    },

    close() {
      if (closed) return;
      closed = true;
      db.close();
    }
  };

  // A pairing that was abandoned is never read again, so its secret would otherwise stay on disk for good.
  for (const [id, record] of await db.entries('pending')) {
    if (now() >= record.expiresAt) await db.delete('pending', id);
  }

  return store;
}

function requireText(value, what) {
  if (typeof value !== 'string' || value.length === 0) throw new TypeError(`${what} is non-empty text.`);
}

/*
 * The adapter contract: `open(name, stores)` resolves to a connection; `deleteDatabase(name)` removes one.
 * A connection has `get(store, key)` (undefined when absent), `entries(store)` ([key, value] pairs),
 * `put(store, key, value)`, `add(store, key, value)` (false, and nothing written, when the key exists),
 * `delete(store, key)` and `close()`. Keys are text or [text, number] arrays. Values are copied in and out,
 * as IndexedDB's structured clone does, so a caller changing what it passed or got changes nothing stored.
 */

/** Memory instead of IndexedDB, for `node --test`. Each call makes a fresh, empty set of databases. */
export function memoryAdapter() {
  const databases = new Map();

  return {
    async open(name, stores) {
      if (!databases.has(name)) databases.set(name, new Map(stores.map((store) => [store, new Map()])));
      const tables = databases.get(name);
      const table = (store) => {
        const rows = tables.get(store);
        if (!rows) throw new Error(`No object store ${store}.`);
        return rows;
      };
      // Array keys compare by value in IndexedDB; a Map compares arrays by identity, so keys are spelled as JSON.
      const id = (key) => JSON.stringify(key);
      return {
        async get(store, key) {
          const row = table(store).get(id(key));
          return row === undefined ? undefined : structuredClone(row.value);
        },
        async entries(store) {
          return [...table(store).values()].map((row) => [structuredClone(row.key), structuredClone(row.value)]);
        },
        async put(store, key, value) {
          table(store).set(id(key), { key: structuredClone(key), value: structuredClone(value) });
        },
        async add(store, key, value) {
          if (table(store).has(id(key))) return false;
          table(store).set(id(key), { key: structuredClone(key), value: structuredClone(value) });
          return true;
        },
        async delete(store, key) {
          table(store).delete(id(key));
        },
        close() {}
      };
    },
    async deleteDatabase(name) {
      databases.delete(name);
    },
    /** The names of the databases that exist, for a test to check what was created. */
    databases: () => [...databases.keys()]
  };
}

const VERSION = 1;

// Each call is its own transaction and resolves only when the transaction completes, so a write has
// landed by the time its promise does.
function transact(db, store, mode, operate) {
  return new Promise((resolve, reject) => {
    const transaction = db.transaction(store, mode);
    let result;
    operate(transaction.objectStore(store), (value) => { result = value; });
    transaction.oncomplete = () => resolve(result);
    transaction.onabort = () => reject(transaction.error ?? new DOMException('The transaction was aborted.', 'AbortError'));
  });
}

function idbConnection(db) {
  const single = (store, mode, makeRequest) => transact(db, store, mode, (objects, done) => {
    const request = makeRequest(objects);
    request.onsuccess = () => done(request.result);
  });

  return {
    get: (store, key) => single(store, 'readonly', (objects) => objects.get(key)),
    entries: (store) => transact(db, store, 'readonly', (objects, done) => {
      const rows = [];
      done(rows);
      const cursor = objects.openCursor();
      cursor.onsuccess = () => {
        if (!cursor.result) return;
        rows.push([cursor.result.primaryKey, cursor.result.value]);
        cursor.result.continue();
      };
    }),
    put: (store, key, value) => single(store, 'readwrite', (objects) => objects.put(value, key)).then(() => undefined),
    add: (store, key, value) => transact(db, store, 'readwrite', (objects, done) => {
      done(true);
      const request = objects.add(value, key);
      request.onerror = (event) => {
        // The key exists. Handled here, so the transaction completes instead of aborting.
        if (request.error?.name !== 'ConstraintError') return;
        event.preventDefault();
        done(false);
      };
    }),
    delete: (store, key) => single(store, 'readwrite', (objects) => objects.delete(key)).then(() => undefined),
    close: () => db.close()
  };
}

/** The browser's adapter. Out-of-line keys in every store; `hostKeys` is keyed [hostId, epoch]. */
export const indexedDbAdapter = Object.freeze({
  open(name, stores) {
    return new Promise((resolve, reject) => {
      const request = indexedDB.open(name, VERSION);
      request.onupgradeneeded = () => {
        for (const store of stores) {
          if (!request.result.objectStoreNames.contains(store)) request.result.createObjectStore(store);
        }
      };
      request.onsuccess = () => {
        const db = request.result;
        // forget() in another tab deletes this database, and the deletion waits until every connection to it
        // has closed: without this, "Forget this device" would hang for as long as a second tab stayed open.
        db.onversionchange = () => db.close();
        resolve(idbConnection(db));
      };
      request.onerror = () => reject(request.error);
    });
  },
  deleteDatabase(name) {
    return new Promise((resolve, reject) => {
      let blocked = 0;
      const request = indexedDB.deleteDatabase(name);
      request.onsuccess = () => {
        clearTimeout(blocked);
        resolve();
      };
      request.onerror = () => {
        clearTimeout(blocked);
        reject(request.error);
      };
      // The request stays queued and may still finish once the other tab closes; the caller is told now.
      request.onblocked = () => {
        clearTimeout(blocked);
        blocked = setTimeout(() => reject(new BlockedError()), BLOCKED_AFTER_MS);
      };
    });
  }
});
