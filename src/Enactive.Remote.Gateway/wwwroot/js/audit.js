// The security log, as the person reads it. No DOM here, so `node --test` runs it.

// What each action the gateway writes means, in words. An action this page has never heard of is shown as
// its own name: a kind of row added to the gateway later shows up looking unfamiliar, which is what it is,
// instead of being dropped from the person's log or read as something else.
const SENTENCES = {
  'signin.github': 'Signed in with GitHub',
  'signin.google': 'Signed in with Google',
  'signin.dev': 'Signed in by name (development sign-in)',
  'signout.everywhere': 'Signed out everywhere',
  'sessions.revoked': 'Signed out everywhere',
  'account.disabled': 'Account disabled',
  'account.enabled': 'Account enabled again',
  'device.registered': 'Device added',
  'device.enrolled': 'Device added with an invitation',
  'device.revoked': 'Device removed',
  'invite.created': 'Invitation to add a device made',
  'host.registered': 'Computer registered',
  'host.revoked': 'Computer removed'
};

/**
 * Who did it, when it was not the person: a row the operator or one of the person's computers wrote reads
 * the same as one of theirs without it, and "Device removed" by a computer is not something they did.
 */
function by(actor) {
  if (actor === 'operator') return ' by the operator';
  if (typeof actor === 'string' && actor.startsWith('host:')) return ' by one of your computers';
  return '';
}

/**
 * One row of `GET /api/audit` as a sentence. The lookup is an own-property one: "constructor" would otherwise
 * find Object's function and print its source.
 */
export function describe(row) {
  return Object.hasOwn(SENTENCES, row.action) ? SENTENCES[row.action] + by(row.actor) : String(row.action);
}

/**
 * The rows, the newest first, as a new array. The gateway sends them in that order; the page does not rely
 * on it, since a log drawn out of order says things happened in an order they did not. A time that cannot be
 * read goes last rather than making the comparison inconsistent, which would scramble the rest.
 */
export function newestFirst(rows) {
  const time = (row) => {
    const at = Date.parse(row.at);
    return Number.isNaN(at) ? -Infinity : at;
  };

  return [...rows].sort((a, b) => {
    const [ta, tb] = [time(a), time(b)];
    return ta === tb ? 0 : ta < tb ? 1 : -1;
  });
}
