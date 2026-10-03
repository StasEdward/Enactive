'use strict';
const statusText = document.getElementById('status');
const errorText = document.getElementById('error');
const signout = document.getElementById('signout');
let csrfToken;
function error(message) {
    errorText.textContent = message;
    errorText.hidden = false;
}
async function loadSession() {
    const response = await fetch('/admin/api/session', { cache: 'no-store', credentials: 'same-origin' });
    if (!response.ok) throw new Error('Session check failed. Refresh the page to try again.');
    const session = await response.json();
    csrfToken = session.csrfToken;
    globalThis.adminDirectory?.setAuthenticated(session.authenticated, session.csrfToken);
    statusText.textContent = session.authenticated ? 'You are signed in as an administrator.' : 'Sign in to continue.';
    document.getElementById('signin').hidden = session.authenticated;
    document.getElementById('session').hidden = !session.authenticated;
    document.getElementById('expires').textContent = session.authenticated
        ? `Session expires ${new Date(session.expiresAt).toLocaleString()}.` : '';
}
signout.addEventListener('click', async () => {
    signout.disabled = true;
    errorText.hidden = true;
    try {
        const response = await fetch('/admin/api/signout', {
            method: 'POST', credentials: 'same-origin', headers: { 'X-CSRF-TOKEN': csrfToken }
        });
        if (!response.ok && response.status !== 401) throw new Error('Sign out failed. Refresh the page and try again.');
        await loadSession();
    } catch (failure) { error(failure.message); }
    finally { signout.disabled = false; }
});
// Only a fixed message: provider errors and query values must never become page markup.
if (new URLSearchParams(location.search).has('error')) error('Sign in was refused. Check your access and MFA with the server operator.');
loadSession().catch(failure => error(failure.message));
