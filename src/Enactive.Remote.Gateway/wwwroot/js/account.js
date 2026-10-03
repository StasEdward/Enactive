// Deleting the account, from the account menu: what the person is asked, what they are told afterwards, and the
// order of the steps. No DOM here, so `node --test` runs it; app.js draws the dialog and the sign-in page.
import { KEYS_KEPT } from './devices.js';

/** Asked before anything is sent. */
export const DELETE_QUESTION =
  'This deletes your account and everything stored for it on the service. Your computers stop. Continue?';

/** Said on the sign-in page once the gateway has deleted the account. */
export const ACCOUNT_DELETED =
  'Your account and everything stored for it on the service are gone. Backups are kept for 30 days and then removed.';

/**
 * Said on the sign-in page when the session was opened too long ago to delete with (the gateway's
 * `reauthenticate`): signing in again is the only thing that helps, so the page signs out and says so.
 */
export const SIGN_IN_TO_DELETE = 'Sign in again, then delete the account from the menu.';

/**
 * Deletes the account (`remove('/api/account')`), and then this browser's keys of it (`keystore.forget()`).
 *
 * The keys only after the gateway has said yes. Deleted first, a refusal - an old session, the gateway out of
 * reach - left an account that this browser could no longer read. A store that cannot be deleted (another tab
 * keeping it open, the browser's storage failing) does not undo anything: the account is gone either way, and
 * the person is told to clear the site's data instead (KEYS_KEPT), as after "Forget this device".
 *
 * Resolves to `{ deleted, sentence }`: the sentence is for the sign-in page. `deleted` is false only for
 * `reauthenticate`; any other refusal is thrown, with nothing of this browser touched.
 */
export async function deleteAccount({ remove, keystore }) {
  try {
    await remove('/api/account');
  } catch (error) {
    if (error?.code === 'reauthenticate') {
      return { deleted: false, sentence: SIGN_IN_TO_DELETE };
    }

    throw error;
  }

  let keysKept = false;

  if (keystore) {
    try {
      await keystore.forget();
    } catch {
      keysKept = true;
    }
  }

  return { deleted: true, sentence: keysKept ? `${ACCOUNT_DELETED} ${KEYS_KEPT}` : ACCOUNT_DELETED };
}
