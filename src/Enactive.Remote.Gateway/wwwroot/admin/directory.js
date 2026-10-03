'use strict';
(() => {
    const element = id => document.getElementById(id);
    const kind = element('directory-kind'), search = element('directory-search'), state = element('directory-state');
    const rows = element('directory-rows'), head = element('directory-head'), status = element('directory-status');
    const previous = element('directory-previous'), next = element('directory-next');
    const details = element('user-details'), fields = element('user-fields'), overview = element('overview');
    let authenticated = false, generation = 0, detailGeneration = 0, cursor = '', history = [], nextCursor = null;
    let activeSearch = '', activeState = '', activeKind = 'users';
    const date = value => value ? new Date(value).toLocaleString() : 'Never';
    function node(tag, text) {
        const result = document.createElement(tag);
        // Names and provider subjects are untrusted; never interpolate them into HTML.
        result.textContent = String(text ?? '');
        return result;
    }
    function clearDetails() { detailGeneration++; details.hidden = true; fields.replaceChildren(); }
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
            const names = activeKind === 'users' ? ['Name', 'Status', 'Stored bytes', 'Registered', 'Details'] : ['Name', 'Provider', 'Subject', 'Status', 'Requested', 'Decided'];
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
        setAuthenticated(value) {
            authenticated = value;
            if (!value) {
                generation++; clear(); overview.textContent = ''; status.textContent = '';
                element('session').hidden = true; element('signin').hidden = false;
                element('status').textContent = 'Sign in to continue.';
                return;
            }
            return reset();
        }
    };
})();
