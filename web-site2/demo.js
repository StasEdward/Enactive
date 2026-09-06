const tabs = Array.from(document.querySelectorAll('[data-stage]'));
const panel = document.getElementById('stage-panel');
const status = document.getElementById('demo-status');
const stages = {
  plan: {status: 'Plan ready', steps: [['01', 'Inspect the existing page and styles', 'Ready'], ['02', 'Build the form and validation', 'Pending'], ['03', 'Run tests and prepare the diff', 'Pending']], activity: 'Plan prepared · 3 steps · awaiting execution'},
  execute: {status: 'Executing', steps: [['✓', 'Inspect the existing page and styles', 'Done'], ['✓', 'Build the form and validation', 'Done'], ['↳', 'Run tests and prepare the diff', 'Running']], activity: 'run_command · npm test -- contact-form · illustrative action'},
  review: {status: 'Ready for review', steps: [['✓', 'Inspect the existing page and styles', 'Done'], ['✓', 'Build the form and validation', 'Done'], ['✓', 'Run tests and prepare the diff', '6 passed']], activity: 'Artifact ready · 2 changed files · review the diff before applying'}
};
function selectStage(tab) {
  const stage = stages[tab.dataset.stage];
  tabs.forEach(item => {const active = item === tab; item.setAttribute('aria-selected', String(active)); item.tabIndex = active ? 0 : -1;});
  panel.setAttribute('aria-labelledby', tab.id);
  status.textContent = stage.status;
  const steps = document.createElement('div'); steps.className = 'steps';
  stage.steps.forEach(([symbol, label, state]) => {
    const row = document.createElement('div');
    const icon = document.createElement('span'); icon.className = 'step-symbol'; icon.textContent = symbol;
    const text = document.createElement('span'); text.textContent = label;
    const meta = document.createElement('small'); meta.textContent = state;
    row.append(icon, text, meta); steps.append(row);
  });
  const activity = document.createElement('div'); activity.className = 'activity mono'; activity.textContent = stage.activity;
  panel.replaceChildren(steps, activity);
}
tabs.forEach((tab, index) => {
  tab.addEventListener('click', () => selectStage(tab));
  tab.addEventListener('keydown', event => {
    let next;
    if(event.key === 'ArrowRight') next = (index + 1) % tabs.length;
    if(event.key === 'ArrowLeft') next = (index + tabs.length - 1) % tabs.length;
    if(event.key === 'Home') next = 0;
    if(event.key === 'End') next = tabs.length - 1;
    if(next !== undefined) {event.preventDefault(); tabs[next].focus(); selectStage(tabs[next]);}
  });
});
