// What the signed-out page offers and says. No DOM here, so `node --test` runs it.

// Only how a provider is NAMED. Whether it is offered is the gateway's answer to /api/providers and
// nothing else: a list of providers kept here would go on offering one the gateway no longer has, as a
// button that ends on a 404.
const LABELS = { github: 'GitHub', google: 'Google' };

/** One sign-in link per provider the gateway offers, in the order it listed them. */
export function providerLinks(names) {
  return names.map((name) => ({
    href: `/auth/${encodeURIComponent(name)}/start`,
    // A provider the gateway added after this page was written is still offered, under its own name,
    // rather than hidden until somebody teaches the page a label for it.
    label: `Continue with ${LABELS[name] ?? name}`
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
