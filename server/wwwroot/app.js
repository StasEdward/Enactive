const $ = selector => document.querySelector(selector);
const escape = value => String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
const terminal = status => ['Completed', 'Failed', 'Incomplete', 'Cancelled', 'Interrupted'].includes(status);
const labels = { WaitingForUser: 'Needs permission', CancelRequested: 'Stopping', DecisionQueued: 'Answer queued', Queued: 'Queued for Host', Pending: 'Needs permission' };
const pill = status => `<span class="pill ${escape(status)}">${escape(labels[status] || status)}</span>`;
const time = date => date ? new Date(date).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }) : 'Never connected';
const empty = (title, text, action = '') => `<div class="empty"><div class="empty-symbol" aria-hidden="true">◇</div><h2>${title}</h2><p>${text}</p>${action}</div>`;
let csrf, state, view = 'tasks', filter = 'all', selectedTask, revokeId, polling = false;
let authenticated = false;
let toastTimer;
function toast(text) { $('#toast').textContent = text; $('#toast').hidden = false; clearTimeout(toastTimer); toastTimer = setTimeout(() => $('#toast').hidden = true, 4500); }
function theme(value) {
  document.documentElement.dataset.theme = value;
  localStorage.setItem('enactive-theme', value);
  document.querySelectorAll('.wordmark').forEach(img => img.src = `/brand/wordmark${value === 'light' ? '-light' : ''}.svg`);
  $('#theme').textContent = value === 'light' ? 'Dark theme' : 'Light theme';
}
theme(localStorage.getItem('enactive-theme') || 'dark');
$('#theme').onclick = () => theme(document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark');

async function api(path, body) {
  const response = await fetch('/api' + path, { method: body === undefined ? 'GET' : 'POST',
    headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf || '' },
    body: body === undefined ? undefined : JSON.stringify(body) });
  if (response.status === 401) { authenticated = false; $('#shell').hidden = true; $('#login').hidden = false; throw new Error('Enter your owner access key to continue.'); }
  if (!response.ok) {
    const problem = await response.json().catch(() => ({}));
    throw new Error(problem.error || (response.status === 429 ? 'Too many attempts. Try again in a minute.' : `Request failed (${response.status}).`));
  }
  return response.status === 204 ? null : response.json().catch(() => null);
}
async function session() {
  const value = await api('/session'); csrf = value.csrfToken; authenticated = value.authenticated;
  $('#shell').hidden = !authenticated; $('#login').hidden = authenticated;
  if (authenticated) await refresh();
}
$('#login-form').onsubmit = async event => {
  event.preventDefault(); const button = event.submitter; button.disabled = true; $('#login-error').textContent = '';
  try { await api('/login', { key: $('#owner-key').value }); $('#owner-key').value = ''; await session(); }
  catch (error) { $('#login-error').textContent = error.message; } finally { button.disabled = false; }
};
$('#logout').onclick = async () => {
  try { await api('/logout', {}); document.querySelectorAll('dialog[open]').forEach(d => d.close()); state = null; await session(); }
  catch (error) { toast(error.message); }
};
async function refresh() {
  if (polling || !authenticated) return;
  polling = true;
  try {
    const next = await api('/state');
    const changed = JSON.stringify(next) !== JSON.stringify(state); state = next;
    $('#gateway-dot').className = 'dot online'; $('#connection-label').textContent = 'Connected'; $('#connection-error').hidden = true;
    if (changed) render();
  } catch (error) {
    $('#gateway-dot').className = 'dot'; $('#connection-label').textContent = 'Reconnecting'; $('#connection-error').hidden = false;
  } finally { polling = false; }
}
setInterval(refresh, 3000);
document.addEventListener('visibilitychange', () => { if (!document.hidden) refresh(); });

function navigate(next) {
  view = ['tasks', 'approvals', 'inbox', 'hosts'].includes(next) ? next : 'tasks';
  const titles = { tasks: ['Tasks', 'Your work, in motion.', 'A clear view of what’s running, waiting, and ready for you.'], approvals: ['Permissions', 'Your decision. Next step.', 'Review the exact action before it runs on your computer.'], inbox: ['Inbox', 'The work reports back.', 'Permission requests and results, collected in one place.'], hosts: ['Computers', 'Where the work happens.', 'Your files and tools stay on the computer you connect.'] };
  const [crumb, title, description] = titles[view];
  $('#breadcrumb').textContent = crumb; $('#page-title').textContent = title; $('#page-description').textContent = description;
  for (const name of Object.keys(titles)) $(`#${name}-view`).hidden = name !== view;
  document.querySelectorAll('[data-view]').forEach(b => b.classList.toggle('active', b.dataset.view === view));
  $('#stats').hidden = view !== 'tasks'; $('#new-task').hidden = view !== 'tasks';
}
document.querySelectorAll('[data-view]').forEach(b => b.onclick = () => { location.hash = b.dataset.view; navigate(b.dataset.view); });
window.addEventListener('hashchange', () => navigate(location.hash.slice(1)));
navigate(location.hash.slice(1));
document.querySelectorAll('[data-filter]').forEach(b => b.onclick = () => {
  filter = b.dataset.filter; document.querySelectorAll('[data-filter]').forEach(t => t.classList.toggle('active', t === b)); renderTasks();
});
$('#search').oninput = renderTasks;
const latest = task => state.runs.filter(r => r.taskId === task.id).at(-1);
function render() {
  $('#task-count').textContent = state.tasks.length;
  $('#approval-count').textContent = state.approvals.filter(a => ['Pending', 'DecisionQueued'].includes(a.status)).length;
  $('#inbox-count').textContent = state.notices.filter(n => !n.read).length;
  const active = state.runs.filter(r => ['Running', 'CancelRequested'].includes(r.status)).length;
  $('#stats').innerHTML = [[active, 'In progress', 'Work underway'], [state.approvals.filter(a => a.status === 'Pending').length, 'Needs your input', 'Actions awaiting permission'], [state.runs.filter(r => r.status === 'Completed').length, 'Completed', 'Results ready to review'], [state.hosts.filter(h => h.online).length, 'Computers online', `${state.hosts.filter(h => !h.revoked).length} registered`]].map(([value, label, foot]) => `<div class="stat"><div class="stat-label">${label}</div><div class="stat-value">${value.toString().padStart(2, '0')}</div><div class="stat-foot">${foot}</div></div>`).join('');
  renderTasks(); renderApprovals(); renderHosts(); renderNotices();
  if ($('#detail-dialog').open) renderDetail();
}
function renderTasks() {
  if (!state) return;
  const search = $('#search').value.toLowerCase();
  const tasks = [...state.tasks].reverse().filter(task => {
    const run = latest(task), status = run?.status || 'Draft';
    return `${task.title} ${task.prompt}`.toLowerCase().includes(search) && (filter === 'all' || filter === 'active' && run && !terminal(status) || filter === 'waiting' && status === 'WaitingForUser' || filter === 'done' && terminal(status));
  });
  const registered = state.hosts.some(h => !h.revoked && h.workspaces.length);
  $('#task-list').innerHTML = tasks.length ? tasks.map(task => {
    const run = latest(task), status = run?.status || 'Draft';
    const host = state.hosts.find(h => h.id === task.hostId);
    const workspace = host?.workspaces.find(w => w.id === task.workspaceId)?.name || task.workspaceId;
    return `<article class="task-card"><div class="task-symbol" aria-hidden="true">${status === 'Completed' ? '✓' : status === 'WaitingForUser' ? '◇' : '↗'}</div><div class="task-content"><button class="task-title" data-detail="${escape(task.id)}">${escape(task.title)}</button><p class="task-preview">${escape(run?.summary || task.prompt)}</p><div class="metadata"><span class="mono">${escape(workspace)}</span><span>${escape(host?.name || 'Unknown computer')}</span><span>${time(run?.createdAt || task.createdAt)}</span></div></div><div class="task-actions">${pill(status)}${!run || terminal(status) ? `<button class="secondary" data-start="${escape(task.id)}" ${host?.revoked ? 'disabled' : ''}>${run ? 'Run again' : 'Run task'} ↗</button>` : `<button class="quiet" data-detail="${escape(task.id)}">Open →</button>`}</div></article>`;
  }).join('') : empty(state.tasks.length ? 'No matching tasks' : 'A place for your next task.', state.tasks.length ? 'Try another filter or search.' : registered ? 'Tell Enactive what needs doing. Your computer will take it from there.' : 'Connect your first computer to make its workspaces available here.', `<button class="secondary" data-empty-action="${registered ? 'task' : 'host'}">${registered ? 'Create a task' : 'Connect a computer'} ↗</button>`);
}
function approvalCard(a) {
  const task = state.tasks.find(t => t.id === state.runs.find(r => r.id === a.runId)?.taskId);
  const actionable = a.status === 'Pending' && new Date(a.expiresAt) > new Date();
  return `<article class="approval-card"><div class="approval-top"><h3>${escape(a.tool)}</h3>${pill(a.status)}</div><p>${escape(task?.title || 'Task')}</p><p class="muted small">${escape(a.reason)}</p><p class="approval-meta">Working directory · <code>${escape(a.workingDirectory)}</code></p><pre tabindex="0" aria-label="Complete tool arguments">${escape(a.arguments)}</pre><p class="approval-meta">Expires ${time(a.expiresAt)} · Call <code>${escape(a.toolCallId)}</code></p>${actionable ? `<label class="read-check"><input type="checkbox" data-review="${escape(a.id)}">I have reviewed the complete action above.</label><div class="approval-buttons"><button class="danger" data-decision="deny" data-approval="${escape(a.id)}">Deny</button><button class="primary" data-decision="allow" data-approval="${escape(a.id)}" disabled>Allow once ↗</button></div>` : `<p class="small muted">${a.status === 'DecisionQueued' ? 'Your answer is saved. Waiting for Host confirmation.' : a.status === 'Pending' ? 'This request has expired. Waiting for Host to reconcile its state.' : 'This request is no longer awaiting a decision.'}</p>`}</article>`;
}
function renderApprovals() {
  const pending = state.approvals.filter(a => ['Pending', 'DecisionQueued'].includes(a.status));
  $('#approval-list').innerHTML = pending.length ? pending.map(approvalCard).join('') : empty('Nothing waiting on you.', 'Requests appear here when a tool needs your permission to continue.');
}
function renderHosts() {
  $('#host-list').innerHTML = state.hosts.length ? state.hosts.map(h => `<article class="host-card"><div class="host-top"><h3>${escape(h.name)}</h3><span class="pill"><span class="dot ${h.online ? 'online' : ''}"></span>${h.revoked ? 'Revoked' : h.online ? 'Online' : 'Offline'}</span></div><p class="small muted">Last seen · ${time(h.lastSeenAt)}</p><div class="host-workspaces">${h.workspaces.length ? h.workspaces.map(w => escape(w.name)).join(' · ') : 'No workspaces published yet'}</div>${!h.revoked ? `<button class="danger" data-revoke="${escape(h.id)}">Revoke access</button>` : ''}</article>`).join('') : empty('Connect your first computer.', 'Register a computer, then configure its Host with the device credential.', '<button class="secondary" data-empty-action="host">Register computer ↗</button>');
}
function renderNotices() {
  $('#notice-list').innerHTML = state.notices.length ? state.notices.map(n => `<article class="notice"><span class="dot ${!n.read ? 'online' : ''}"></span><div class="notice-content"><h3>${escape(n.title)}</h3><p class="muted">${escape(n.detail)}</p><time>${time(n.at)}</time></div><button class="quiet" data-run-detail="${escape(n.runId)}">Open task →</button></article>`).join('') : empty('All quiet for now.', 'Task results and permission requests will be saved here.');
}
function renderDetail() {
  const task = state.tasks.find(t => t.id === selectedTask); if (!task) return;
  const run = latest(task);
  $('#task-detail').innerHTML = `<h2 class="detail-title">${escape(task.title)}</h2>${pill(run?.status || 'Draft')}<h3 class="detail-section">Instructions</h3><pre>${escape(task.prompt)}</pre>${run?.summary ? `<h3>Result</h3><pre>${escape(run.summary)}</pre>` : ''}${run && !terminal(run.status) ? `<div class="dialog-actions"><button class="danger" data-cancel="${escape(run.id)}" ${run.status === 'CancelRequested' ? 'disabled' : ''}>${run.status === 'CancelRequested' ? 'Cancellation requested' : 'Stop run'}</button></div>` : ''}${run ? state.approvals.filter(a => a.runId === run.id && ['Pending', 'DecisionQueued'].includes(a.status)).map(approvalCard).join('') : ''}<h3>Timeline</h3><ol class="timeline">${state.events.filter(e => state.runs.some(r => r.taskId === task.id && r.id === e.runId)).map(e => `<li><time>${time(e.at)}</time><strong>${escape(labels[e.kind] || e.kind)}</strong><p>${escape(e.detail)}</p></li>`).join('') || '<li class="muted">No Host events yet.</li>'}</ol>`;
}
function openTask() {
  $('#task-form').reset(); $('#task-error').textContent = '';
  const workspaces = state.hosts.filter(h => !h.revoked).flatMap(h => h.workspaces.map(w => `<option value="${escape(h.id)}:${escape(w.id)}">${escape(w.name)} · ${escape(h.name)}${h.online ? '' : ' (offline)'}</option>`));
  if (!workspaces.length) { navigate('hosts'); toast('Connect a Host and publish a workspace first.'); return; }
  $('#workspace').innerHTML = workspaces.join(''); $('#task-dialog').showModal();
}
function openHost() { $('#host-form').reset(); $('#host-secret').hidden = true; $('#host-error').textContent = ''; $('#register').hidden = false; $('#host-name').readOnly = false; $('#host-dialog').showModal(); }
$('#new-task').onclick = openTask; $('#add-host').onclick = openHost;
$('#host-dialog').addEventListener('close', () => { $('#host-token').value = ''; });
$('#host-form').onsubmit = async event => {
  event.preventDefault(); const button = event.submitter; button.disabled = true;
  try { const host = await api('/hosts', { name: $('#host-name').value }); $('#host-id').value = host.id; $('#host-token').value = host.token; $('#host-secret').hidden = false; $('#register').hidden = true; $('#host-name').readOnly = true; await refresh(); }
  catch (e) { $('#host-error').textContent = e.message; } finally { button.disabled = false; }
};
$('#task-form').onsubmit = async event => {
  event.preventDefault(); const buttons = [...$('#task-form').querySelectorAll('[type=submit]')]; buttons.forEach(b => b.disabled = true);
  try {
    const value = $('#workspace').value, split = value.indexOf(':');
    const task = await api('/tasks', { hostId: value.slice(0, split), workspaceId: value.slice(split + 1), title: $('#task-title').value, prompt: $('#prompt').value });
    if (event.submitter.value === 'run') {
      try { await api(`/tasks/${task.id}/start`, { commandId: crypto.randomUUID() }); }
      catch (error) { toast(`Draft saved; start was not confirmed. Open it to check: ${error.message}`); }
    }
    $('#task-dialog').close(); await refresh();
  } catch (error) { $('#task-error').textContent = error.message; } finally { buttons.forEach(b => b.disabled = false); }
};
$('#mark-read').onclick = async () => { try { await api('/notices/read', {}); await refresh(); } catch (error) { toast(error.message); } };
$('#confirm-form').onsubmit = async event => { event.preventDefault(); event.submitter.disabled = true; try { await api(`/hosts/${revokeId}/revoke`, {}); $('#confirm-dialog').close(); await refresh(); } catch (error) { toast(error.message); } finally { event.submitter.disabled = false; } };
document.addEventListener('change', event => {
  if (event.target.dataset.review) event.target.closest('.approval-card').querySelector('[data-decision=allow]').disabled = !event.target.checked;
});
document.addEventListener('click', async event => {
  const button = event.target.closest('button'); if (!button) return;
  const d = button.dataset;
  if (d.close) return $(`#${d.close}`).close();
  if (d.emptyAction) return d.emptyAction === 'host' ? openHost() : openTask();
  if (d.detail || d.runDetail) {
    selectedTask = d.detail || state.runs.find(r => r.id === d.runDetail)?.taskId; renderDetail(); $('#detail-dialog').showModal(); return;
  }
  if (d.revoke) { revokeId = d.revoke; $('#confirm-dialog').showModal(); return; }
  if (!d.start && !d.cancel && !d.decision) return;
  button.disabled = true;
  // Preserve the id on the same control after an ambiguous network response.
  const commandId = d.commandId ||= crypto.randomUUID();
  try {
    if (d.start) await api(`/tasks/${d.start}/start`, { commandId });
    if (d.cancel) await api(`/runs/${d.cancel}/cancel`, { commandId });
    if (d.decision) { const approval = state.approvals.find(a => a.id === d.approval); await api(`/approvals/${approval.id}/resolve`, { commandId, decision: d.decision, actionHash: approval.actionHash }); }
    toast(d.decision ? 'Answer queued for Host confirmation.' : d.cancel ? 'Cancellation requested.' : 'Task queued for your computer.');
    await refresh();
  } catch (error) { toast(error.message); button.disabled = false; }
});
session().catch(error => { $('#login').hidden = false; $('#login-error').textContent = error.message; });
