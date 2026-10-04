'use strict';
(() => {
    const get = id => document.getElementById(id);
    const panel = get('quota-panel'), rows = get('quota-rows'), status = get('quota-status');
    const form = get('quota-form'), reason = get('quota-reason'), save = get('quota-save');
    const confirmation = get('quota-confirmation'), confirm = get('quota-confirm');
    let authenticated = false, token, owner = null, ownerName = '', view, inputs = new Map(), generation = 0, busy = false, pending;
    const labels = { HostsPerUser: 'Computers', DevicesPerUser: 'Devices', ActiveRunsPerUser: 'Active runs',
        QueuedCommandsPerHost: 'Queued commands per computer', TasksPerDay: 'Tasks created in the last 24 hours',
        OpenInvitesPerUser: 'Open invitations', SealedBytesPerUser: 'Encrypted storage bytes' };
    function node(tag, text) {
        const item = document.createElement(tag); item.textContent = String(text ?? ''); return item;
    }
    function path() { return owner ? '/admin/api/users/' + encodeURIComponent(owner) + '/quotas' : '/admin/api/quotas'; }
    function cancel() { pending = null; confirmation.hidden = true; }
    function clear() { generation++; view = null; inputs.clear(); rows.replaceChildren(); get('quota-queues').replaceChildren(); cancel(); panel.hidden = true; }
    async function read(url) {
        const response = await fetch(url, { cache: 'no-store', credentials: 'same-origin' });
        if (response.status === 401 || response.status === 403) {
            globalThis.adminDirectory?.setAuthenticated(false); globalThis.adminQuotas.setAuthenticated(false);
            throw new Error('Your session ended. Sign in again.');
        }
        if (!response.ok) throw new Error('Could not load quotas. Refresh to retry.');
        return response.json();
    }
    async function queues(after, version) {
        if (!owner) return;
        const container = get('quota-queues');
        const data = await read('/admin/api/users/' + encodeURIComponent(owner) + '/queues?after=' + encodeURIComponent(after ?? ''));
        if (!authenticated || version !== generation) return;
        container.replaceChildren(node('h3', 'Queues by computer'));
        for (const queue of data.items) container.append(node('p', `${queue.name} (${queue.hostId}): ${queue.pending} ordinary commands; ${queue.deviceChanges} device changes; ${queue.cancellations ?? 0} cancellations.`));
        if (data.next) {
            const next = node('button', 'Next computers'); next.type = 'button';
            next.addEventListener('click', () => queues(data.next, version).catch(error => { if (version === generation) status.textContent = error.message; })); container.append(next);
        }
    }
    async function open(id = null, name = '') {
        if (!authenticated || busy) return;
        if (id !== owner || name) ownerName = name || id || '';
        clear(); owner = id; panel.hidden = false; const version = generation;
        get('quota-title').textContent = owner ? `Quotas for ${ownerName || owner}` : 'Shared quota defaults';
        status.textContent = 'Loading…'; save.disabled = true; reason.value = ''; get('quota-reauth').hidden = true;
        try {
            const data = await read(path());
            if (!authenticated || version !== generation) return;
            view = data;
            for (const item of data.items) {
                const row = node('tr', '');
                row.append(node('th', labels[item.key]), node('td', item.usage === null ? (item.key === 'QueuedCommandsPerHost' && owner ? 'See queues below' : '—') : item.usage + (item.overLimit ? ' · Over limit' : '')),
                    node('td', `${item.effective} (${item.source})`));
                const cell = node('td', ''), input = node('input', '');
                // Decimal strings preserve 64-bit byte limits; Number would silently round them.
                input.type = 'text'; input.inputMode = 'numeric'; input.pattern = '[0-9]+'; input.maxLength = 19;
                input.value = item.override ?? ''; input.placeholder = owner ? item.default : item.startup;
                input.setAttribute('aria-label', labels[item.key] + ' override');
                input.addEventListener('input', cancel);
                inputs.set(item.key, input); cell.append(input); row.append(cell); rows.append(row);
            }
            save.disabled = false; status.textContent = 'Clear a value to inherit. Limits must be positive whole numbers.';
            await queues(null, version);
        } catch (error) { if (version === generation) status.textContent = error.message; }
    }
    reason.addEventListener('input', cancel);
    get('quota-defaults').addEventListener('click', () => open());
    get('quota-refresh').addEventListener('click', () => open(owner));
    get('quota-cancel').addEventListener('click', cancel);
    get('quota-reset').addEventListener('click', () => { if (!busy) { cancel(); for (const input of inputs.values()) input.value = ''; } });
    form.addEventListener('submit', event => {
        event.preventDefault(); if (!view || busy || !authenticated || save.disabled) return;
        const values = {}; const summary = [];
        for (const item of view.items) {
            const text = inputs.get(item.key).value.trim();
            if (text && (!/^[0-9]+$/.test(text) || /^0+$/.test(text))) { status.textContent = 'Use positive whole numbers or leave blank to inherit.'; return; }
            const value = text || null;
            if (value !== item.override) { values[item.key] = value; summary.push(`${labels[item.key]}: ${item.override ?? 'inherited'} → ${value ?? 'inherited'}`); }
        }
        if (!summary.length) { status.textContent = 'No quota changes.'; return; }
        if (!reason.value.trim()) { status.textContent = 'Enter a reason for this change.'; return; }
        pending = { path: path(), body: { expectedVersion: view.version, expectedDefaultsVersion: view.defaultsVersion, values, reason: reason.value.trim() } };
        get('quota-description').textContent = summary.join('; ') + '. Existing resources are preserved; new creation is restricted when usage exceeds the limit.';
        confirmation.hidden = false;
    });
    confirm.addEventListener('click', async () => {
        if (!pending || busy || !authenticated) return;
        const change = pending, version = generation; busy = true; confirm.disabled = true; save.disabled = true;
        for (const input of inputs.values()) input.disabled = true; reason.disabled = true;
        status.textContent = 'Saving…';
        try {
            const response = await fetch(change.path, { method: 'POST', credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': token }, body: JSON.stringify(change.body) });
            if (!authenticated || version !== generation) return;
            if (response.status === 401) { globalThis.adminDirectory?.setAuthenticated(false); globalThis.adminQuotas.setAuthenticated(false); return; }
            if (response.status === 403) { get('quota-reauth').hidden = false; throw new Error('Authenticate again with MFA, then refresh and review before saving.'); }
            if (response.status === 409) throw new Error('Quotas changed. Refresh and review before saving. Your input is preserved.');
            if (response.status === 400) { save.disabled = false; throw new Error('Values were refused. Check the supported ranges and reason. Your input is preserved.'); }
            if (!response.ok) throw new Error('Change was not confirmed. Refresh before trying again. Your input is preserved.');
            busy = false; await open(owner); if (authenticated && view) status.textContent = 'Quotas saved and reloaded.';
        } catch (error) {
            // A lost response may follow a commit. Keep input but require a fresh read; never resend automatically.
            if (authenticated && version === generation) status.textContent = error.message === 'Failed to fetch'
                ? 'Outcome unknown. Refresh before trying again. Your input is preserved.' : error.message;
        } finally { busy = false; confirm.disabled = false; reason.disabled = false;
            for (const input of inputs.values()) input.disabled = false; cancel(); }
    });
    globalThis.adminQuotas = {
        open,
        closeUser() { if (owner) clear(); },
        setAuthenticated(value, csrf) { authenticated = value; token = csrf; if (!value) { clear(); reason.value = ''; status.textContent = ''; } }
    };
})();
