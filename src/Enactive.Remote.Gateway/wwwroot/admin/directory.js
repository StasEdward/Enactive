'use strict';
(() => {
    const element = id => document.getElementById(id);
    const kind = element('directory-kind'), search = element('directory-search'), state = element('directory-state');
    const rows = element('directory-rows'), head = element('directory-head'), status = element('directory-status');
    const previous = element('directory-previous'), next = element('directory-next');
    const details = element('user-details'), fields = element('user-fields'), overview = element('overview');
    let authenticated = false, generation = 0, detailGeneration = 0, cursor = '', history = [], nextCursor = null;
    const actions = element('user-actions'), confirmation = element('access-confirmation');
    const confirmButton = element('access-confirm'), changeStatus = element('access-status'), reauth = element('access-reauth');
    let pending = null, changing = false, csrfToken;
    function cancelChange() { pending = null; confirmation.hidden = true; }
    function offer(path, body, description) {
        if (changing || !authenticated) return;
        pending = { path, body }; confirmation.hidden = false;
        confirmButton.disabled = false; changeStatus.textContent = ''; reauth.hidden = true;
        element('access-description').textContent = description;
        confirmation.scrollIntoView?.({ block: 'nearest' });
    }
    function actionButton(label, handler) {
        const button = node('button', label); button.type = 'button';
        button.addEventListener('click', handler); return button;
    }
    element('access-cancel').addEventListener('click', cancelChange);
    confirmButton.addEventListener('click', async () => {
        if (!pending || changing || !authenticated) return;
        const change = pending; changing = true; confirmButton.disabled = true;
        changeStatus.textContent = 'Saving…';
        try {
            const response = await fetch(change.path, { method: 'POST', credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrfToken }, body: JSON.stringify(change.body) });
            if (!authenticated) return;
            cancelChange();
            if (response.status === 403) {
                reauth.hidden = false;
                throw new Error('A recent MFA login is required. Authenticate again, then review and repeat the action.');
            }
            if (response.status === 401) {
                globalThis.adminDirectory.setAuthenticated(false);
                throw new Error('Your session ended. Sign in again.');
            }
            if (response.status === 409) throw new Error('The record changed. Use Search / refresh and review it before trying again.');
            if (response.status === 404) throw new Error('The record no longer exists. Use Search / refresh.');
            if (!response.ok) throw new Error('Change was not confirmed. Refresh before trying again.');
            const result = await response.json();
            changeStatus.textContent = result.hasAccount
                ? 'Decision saved. An account already exists; refusal does not disable it. Use the user access controls to block access.'
                : result.action === 'disable' ? `Account disabled. ${result.withdrawnCommands} queued commands withdrawn. Already running local work may continue.`
                : 'Change saved.';
            await load();
        } catch (error) {
            cancelChange();
            // An interrupted response may follow a committed change. Never retry automatically.
            if (authenticated) changeStatus.textContent = error.message === 'Failed to fetch'
                ? 'Outcome unknown. Refresh and review the record before trying again.' : error.message;
        } finally { changing = false; confirmButton.disabled = false; }
    });
    let activeSearch = '', activeState = '', activeKind = 'users';
    const date = value => value ? new Date(value).toLocaleString() : 'Never';
    function node(tag, text) {
        const result = document.createElement(tag);
        // Names and provider subjects are untrusted; never interpolate them into HTML.
        result.textContent = String(text ?? '');
        return result;
    }
    function clearDetails() { globalThis.adminQuotas?.closeUser(); detailGeneration++; details.hidden = true; fields.replaceChildren(); actions.replaceChildren(); cancelChange(); }
    function clear() {
        rows.replaceChildren(); head.replaceChildren(); clearDetails();
        previous.disabled = true; next.disabled = true;
    }
    async function read(path) {
        const response = await fetch(path, { cache: 'no-store', credentials: 'same-origin' });
        if (response.status === 401 || response.status === 403) {
            globalThis.adminDirectory.setAuthenticated(false);
            throw new Error('Your administrator session has ended. Sign in again.');
        }
        if (!response.ok) throw new Error(response.status === 404 ? 'This account no longer exists.' : 'Could not load data. Use Search / refresh to retry.');
        return response.json();
    }
    async function showUser(id) {
        clearDetails();
        const version = detailGeneration;
        details.hidden = false;
        fields.append(node('dt', 'Loading…'));
        try {
            const data = await read('/admin/api/users/' + encodeURIComponent(id));
            if (!authenticated || version !== detailGeneration) return;
            fields.replaceChildren();
            for (const [label, value] of [['Name', data.user.displayName], ['ID', data.user.id], ['Status', data.user.status],
                ['Registered', date(data.user.createdAt)], ['Stored bytes', data.user.sealedBytes], ['Computers', data.hosts],
                ['Devices', data.devices], ['Tasks', data.tasks], ['Runs', data.runs], ['Last computer contact', date(data.lastHostSeenAt)]]) {
                fields.append(node('dt', label), node('dd', value));
            }
            const accessPath = '/admin/api/users/' + encodeURIComponent(id) + '/access';
            const disable = data.user.status === 'Active';
            actions.append(actionButton(disable ? 'Disable account' : 'Enable account', () => offer(accessPath,
                { action: disable ? 'disable' : 'enable', expectedVersion: data.user.version },
                `${disable ? 'Disable' : 'Enable'} ${data.user.displayName} (${id})? ` + (disable
                    ? 'This revokes browser sessions and withdraws queued commands. Already running local work may continue.'
                    : 'The owner can sign in again. Revoked sessions and withdrawn commands stay revoked.'))),
                actionButton('Revoke sessions', () => offer(accessPath,
                    { action: 'revoke-sessions', expectedVersion: data.user.version },
                    `End all browser sessions for ${data.user.displayName} (${id})? This does not revoke computer credentials or stop local work.`)));
            actions.append(actionButton('Edit quotas', () => globalThis.adminQuotas?.open(id, data.user.displayName)));
        } catch (error) {
            if (version === detailGeneration) fields.replaceChildren(node('dt', error.message));
        }
    }
    async function load() {
        const version = ++generation;
        clear(); status.textContent = 'Loading…';
        const query = new URLSearchParams({ search: activeSearch, state: activeState, after: cursor, size: '25' });
        try {
            const [page, totals] = await Promise.all([read('/admin/api/' + activeKind + '?' + query), read('/admin/api/overview')]);
            // A slow earlier request must not overwrite a new search or restore data after sign-out.
            if (!authenticated || version !== generation) return;
            overview.textContent = `${totals.users} users · ${totals.disabledUsers} disabled · ${totals.waitingRegistrations} waiting registrations`;
            const header = node('tr', '');
            const names = activeKind === 'users' ? ['Name', 'Status', 'Stored bytes', 'Registered', 'Details'] : ['Name', 'Provider', 'Subject', 'Status', 'Requested', 'Decided', 'Actions'];
            names.forEach(name => header.append(node('th', name))); head.append(header);
            for (const item of page.items) {
                const row = node('tr', '');
                const values = activeKind === 'users' ? [item.displayName, item.status, item.sealedBytes, date(item.createdAt)]
                    : [item.display, item.provider, item.subject, item.state, date(item.requestedAt), item.decidedAt ? date(item.decidedAt) : 'Pending'];
                values.forEach(value => row.append(node('td', value)));
                if (activeKind === 'users') {
                    const cell = node('td', ''), button = node('button', 'View'); button.type = 'button';
                    button.addEventListener('click', () => showUser(item.id)); cell.append(button); row.append(cell);
                }
                if (activeKind === 'registrations') {
                    const cell = node('td', '');
                    for (const [decision, label, finalState] of [['approve', 'Approve', 'Approved'], ['refuse', 'Refuse', 'Refused']]) {
                        if (item.state === finalState) continue;
                        cell.append(actionButton(label, () => offer('/admin/api/registrations/decision',
                            { provider: item.provider, subject: item.subject, decision, expectedVersion: item.version },
                            `${label} registration for ${item.display} (${item.provider}:${item.subject})? ` +
                            'Refusing registration does not disable an existing account.')));
                    }
                    row.append(cell);
                }
                rows.append(row);
            }
            nextCursor = page.next;
            previous.disabled = history.length === 0; next.disabled = !nextCursor;
            status.textContent = page.items.length ? `Page ${history.length + 1} · ${page.items.length} results` : 'No matching records.';
        } catch (error) {
            if (version === generation || !authenticated) status.textContent = error.message;
            if (authenticated && version === generation) previous.disabled = history.length === 0;
        }
    }
    function reset() {
        cursor = ''; history = []; nextCursor = null;
        activeSearch = search.value; activeState = state.value; activeKind = kind.value;
        if (authenticated) return load();
    }
    element('directory-filter').addEventListener('submit', event => { event.preventDefault(); return reset(); });
    kind.addEventListener('change', () => {
        state.replaceChildren(node('option', 'All statuses')); state.children[0].value = '';
        for (const value of kind.value === 'users' ? ['Active', 'Disabled'] : ['Waiting', 'Approved', 'Refused']) {
            const option = node('option', value); option.value = value; state.append(option);
        }
        state.value = kind.value === 'users' ? '' : 'Waiting';
        return reset();
    });
    next.addEventListener('click', () => { if (!authenticated || next.disabled) return; history.push(cursor); cursor = nextCursor; return load(); });
    previous.addEventListener('click', () => { if (!authenticated || previous.disabled) return; cursor = history.pop(); return load(); });
    globalThis.adminDirectory = {
        setAuthenticated(value, token) {
            authenticated = value; csrfToken = token;
            globalThis.adminQuotas?.setAuthenticated(value, token);
            if (!value) {
                generation++; clear(); overview.textContent = ''; status.textContent = ''; changeStatus.textContent = ''; reauth.hidden = true;
                element('session').hidden = true; element('signin').hidden = false;
                element('status').textContent = 'Sign in to continue.';
                return;
            }
            return reset();
        }
    };
})();
