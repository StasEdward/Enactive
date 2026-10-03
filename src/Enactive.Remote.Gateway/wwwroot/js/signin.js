// What the signed-out page offers and says. No DOM here, so `node --test` runs it.

// Only how a provider is NAMED. Whether it is offered is the gateway's answer to /api/providers and
// nothing else: a list of providers kept here would go on offering one the gateway no longer has, as a
// button that ends on a 404.
const LABELS = { github: 'GitHub', google: 'Google' };

// What a provider name may look like. The gateway's names are short lowercase words, and the page puts a
// name into a link and looks its label up by it: a name outside this shape is either a gateway this page
// does not understand or something that is not a name, and neither should become a button. The label
// lookup is an own-property one as well, since "constructor" would otherwise find Object's function and
// print its source as a label.
const NAME = /^[a-z][a-z0-9-]{0,31}$/;

/**
 * One sign-in link per provider the gateway offers, in the order it listed them. A name of the wrong
 * shape is skipped and reported through `warn`.
 */
export function providerLinks(names, warn = console.warn) {
  return names.filter((name) => {
    const ok = typeof name === 'string' && NAME.test(name);
    if (!ok) warn(`Not offering the sign-in provider ${JSON.stringify(name)}: not a provider name.`);
    return ok;
  }).map((name) => ({
    href: `/auth/${encodeURIComponent(name)}/start`,
    // A provider the gateway added after this page was written is still offered, under its own name,
    // rather than hidden until somebody teaches the page a label for it.
    label: `Continue with ${Object.hasOwn(LABELS, name) ? LABELS[name] : name}`
  }));
}

// Where a sign-in that did not open a session ends: the gateway redirects to the page with one of these
// as the fragment. A fragment is never sent to a server, so none of them reaches a log.
const OUTCOMES = {
  waiting: 'Your sign-in is recorded; access is opened by hand during the beta.',
  refused: 'This sign-in was not given access to this gateway.',
  disabled: 'This account has been disabled on this gateway.',
  failed: 'The sign-in did not complete. Try again.'
};

/** The sentence for a `#fragment` the gateway's sign-in ended on, or null for any other fragment. */
export function outcomeOf(hash) {
  const name = (hash ?? '').replace(/^#/, '');
  return Object.hasOwn(OUTCOMES, name) ? OUTCOMES[name] : null;
}

/**
 * Whether the gateway has the development sign-in, asked without attempting one.
 *
 * The route is POST-only, so a GET is answered by routing: 405 where it exists, 404 where it does not,
 * and no limit counts it. The first probe posted an empty name instead, which the sign-in limit counted:
 * every signed-out page load on localhost spent one of the address's sign-ins a minute. Only an answer
 * from the gateway is remembered. A probe that got none - the network, or a reset that dropped it - is
 * asked again next time, rather than hiding the form for the life of the page.
 *
 * @param get the panel's GET; it rejects with an error carrying `status` for a gateway's refusal.
 */
export function createDevelopmentProbe(get) {
  let known = null;

  return async function offered() {
    if (known !== null) return known;

    try {
      await get('/api/dev/sign-in');
      known = false;
    } catch (error) {
      if (typeof error?.status !== 'number') return false;
      known = error.status === 405;
    }

    return known;
  };
}
