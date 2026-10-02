// Talking to the gateway: every request the panel makes goes through here.
//
// Two faults are the page's business rather than the caller's. A 401 means the session is over - it
// expired, it was signed out somewhere else, or "sign out everywhere" reached it - and a 403
// `device-revoked` means this browser was removed from the account. Either one, from whichever call
// happened to notice first, has to end with the same screen, so the call reports it to the hooks below
// instead of every caller deciding what a 401 means. The caller still gets a Refused, and drops it.
//
// Every request is tied to the session it started under (see session-guard.js). After `endSession`
// none of them is read: the in-flight ones are aborted, and one that answers anyway is a Stale.
import { createGuard, Stale } from './session-guard.js';

export { Stale };

/**
 * A refusal the gateway coded, kept apart from a network failure. `retryAfter` is the gateway's Retry-After in
 * seconds when it sent one (a limit says when asking again will work), and undefined when it did not.
 */
export class Refused extends Error {
  constructor(code, message, status, retryAfter) {
    super(message);
    this.code = code;
    this.status = status;
    this.retryAfter = retryAfter;
  }
}

const guard = createGuard();
let controller = new AbortController();
let csrf = '';

const hooks = { unauthenticated: () => {}, deviceRevoked: () => {} };

/** Called once per 401 of the current session, before the caller sees its Refused. */
export function onUnauthenticated(handler) { hooks.unauthenticated = handler; }

/** Called once per 403 `device-revoked` of the current session, before the caller sees its Refused. */
export function onDeviceRevoked(handler) { hooks.deviceRevoked = handler; }

/**
 * Ends the session as far as this page is concerned: aborts every request in flight, drops every answer
 * still on its way, and forgets the antiforgery token, which was bound to the person who has gone.
 */
export function endSession() {
  abandonRequests();
  csrf = '';
}

/**
 * Drops every request in flight and every answer still on its way, but keeps the token. For a sign-out
 * about to be sent: a poll answered 401 while the sign-out is in flight would otherwise run the sign-out
 * hook, abort the sign-out's own request and report it as failed.
 */
export function abandonRequests() {
  guard.bump();
  controller.abort();
  controller = new AbortController();
}

/** The generation requests start under now, for a caller holding a result across an await of its own. */
export function generation() { return guard.generation; }

/** Whether a result obtained under `started` may still be shown. */
export function isCurrent(started) { return guard.isCurrent(started); }

// `headers` adds to what every call sends: a call made as this browser's device names it (X-Enactive-Device).
// `antiforgery` sends the token with a GET the gateway gives only to the panel - the data export, which a link on
// another site would otherwise start with the session cookie alone.
export function get(path, { headers, antiforgery = false } = {}) {
  return request(path, {
    headers: { ...headers, accept: 'application/json', ...(antiforgery ? { 'X-CSRF-TOKEN': csrf } : {}) }
  });
}

export function post(path, body, { headers } = {}) {
  return request(path, {
    method: 'POST',
    headers: { ...headers, 'content-type': 'application/json', 'X-CSRF-TOKEN': csrf },
    body: JSON.stringify(body ?? {})
  });
}

/** A DELETE, with the antiforgery token like every other call that changes something. */
export function remove(path) {
  return request(path, { method: 'DELETE', headers: { accept: 'application/json', 'X-CSRF-TOKEN': csrf } });
}

/**
 * Who is signed in, and the antiforgery token for what they send next. Anonymous on the gateway, so it
 * is also how the page learns that nobody is.
 */
export async function session() {
  const view = await get('/api/session');
  csrf = view.csrfToken;
  return view;
}

async function request(path, init) {
  const started = guard.generation;
  let response;

  try {
    response = await fetch(path, { ...init, signal: controller.signal });
  } catch (error) {
    // Aborted by endSession, or failed after it: either way nobody is waiting for this any more.
    guard.check(started);
    throw error;
  }

  return await unwrap(response, started);
}

/**
 * The body of a reply, or the Refused it carried. A 401 or a `device-revoked` is reported to its hook
 * first - unless the session it belonged to has already ended, when it is only Stale: Alice's 401
 * arriving after Bob signed in must not sign Bob out.
 */
export async function unwrap(response, started = guard.generation) {
  const text = await response.text();
  guard.check(started);

  if (response.status === 401) {
    hooks.unauthenticated();
    throw new Refused('unauthenticated', 'Sign in again.', 401);
  }

  if (!response.ok) {
    const body = parse(text);

    if (response.status === 403 && body?.code === 'device-revoked') {
      hooks.deviceRevoked();
    }

    // The gateway's faults carry a code, and the page says what the code means rather than repeating
    // a sentence written for a log.
    throw new Refused(body?.code ?? 'unknown', body?.error ?? 'That did not work.', response.status,
      retryAfter(response));
  }

  return text ? JSON.parse(text) : null;
}

// Retry-After as a number of seconds, which is the only form the gateway sends. Without it a refusal for asking
// too often could only say "later", and a person told to wait an hour has no way to know which hour.
function retryAfter(response) {
  const seconds = Number(response.headers?.get('Retry-After') ?? NaN);
  return Number.isFinite(seconds) && seconds >= 0 ? seconds : undefined;
}

// A refusal that is not JSON - a 404 from a route that does not exist, a proxy's error page - is a
// refusal without a code, not a SyntaxError that hides which status it was. A success that is not JSON
// still throws: there is nothing in it the caller could use.
function parse(text) {
  if (!text) return null;

  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}
