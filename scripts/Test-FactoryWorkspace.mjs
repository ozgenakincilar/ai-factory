// End-to-end check of the Product Workspace in a real headless Edge: Keycloak sign-in (PKCE),
// project list, a decomposed run, the blocking plan-approval popup and live updates until the run
// finishes. Requires Keycloak, the API (Run-FactoryWorkspace.ps1), the web dev server and a worker.
// Never prints credentials or tokens. Usage: node scripts/Test-FactoryWorkspace.mjs [project]
import { readFile, mkdir, rm } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { resolve } from 'node:path';

const project = process.argv[2] ?? 'factory-workspace';
const app = 'http://127.0.0.1:4200/';
const settings = JSON.parse(await readFile(resolve('artifacts/keycloak/development.json'), 'utf8'));
const profile = resolve('artifacts/keycloak/workspace-browser-profile');
await rm(profile, { recursive: true, force: true });
await mkdir(profile, { recursive: true });
const browser = spawn('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  ['--headless=new', '--disable-gpu', '--no-first-run', '--remote-debugging-address=127.0.0.1', '--remote-debugging-port=58083',
   '--user-data-dir=' + profile, 'about:blank'], { windowsHide: true, stdio: 'ignore' });
const sleep = ms => new Promise(r => setTimeout(r, ms));
let socket, id = 0;
const pending = new Map();
// Page errors (truncated) are reported on failure; they never contain credentials.
const pageErrors = [];
function send(method, params = {}) {
  return new Promise((ok, fail) => { const n = ++id; pending.set(n, { ok, fail }); socket.send(JSON.stringify({ id: n, method, params })); });
}
async function evaluate(expression) {
  const r = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  if (r.exceptionDetails) throw new Error('page_evaluation_failed');
  return r.result.value;
}
async function until(description, expression, timeoutMs = 30000) {
  const end = Date.now() + timeoutMs;
  while (Date.now() < end) { try { if (await evaluate(expression)) return; } catch { /* page navigating */ } await sleep(250); }
  throw new Error('timeout: ' + description);
}
const step = text => console.log('ok  ' + text);
try {
  let target;
  for (let i = 0; i < 40 && !target; i++) {
    try { target = (await (await fetch('http://127.0.0.1:58083/json/list')).json()).find(t => t.type === 'page'); } catch { await sleep(250); }
  }
  socket = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((ok, fail) => { socket.onopen = ok; socket.onerror = fail; });
  socket.onmessage = event => {
    const message = JSON.parse(event.data);
    if (message.method === 'Runtime.exceptionThrown') pageErrors.push(String(message.params.exceptionDetails?.exception?.description ?? message.params.exceptionDetails?.text).slice(0, 300));
    if (message.method === 'Runtime.consoleAPICalled' && message.params.type === 'error')
      pageErrors.push(message.params.args.map(a => String(a.value ?? a.description ?? '')).join(' ').slice(0, 300));
    if (message.id && pending.has(message.id)) { const p = pending.get(message.id); pending.delete(message.id); message.error ? p.fail(new Error(message.error.message)) : p.ok(message.result); }
  };
  await send('Page.enable'); await send('Runtime.enable');
  await send('Page.navigate', { url: app });
  await until('sign-in page', `document.querySelector('.welcome button') !== null`);
  step('signed-out users see only the sign-in page');
  await evaluate(`document.querySelector('.welcome button').click()`);
  await until('Keycloak login form', `location.host === '127.0.0.1:58080' && document.querySelector('#username') !== null`);
  await evaluate(`(() => { const set = (s, v) => { const e = document.querySelector(s); e.value = v; e.dispatchEvent(new Event('input', { bubbles: true })); };
    set('#username', ${JSON.stringify('factory-reviewer')}); set('#password', ${JSON.stringify(settings.HumanPassword)});
    document.querySelector('#kc-login').click(); })()`);
  await until('project list', `location.host === '127.0.0.1:4200' && [...document.querySelectorAll('.projects a')].some(a => a.textContent.trim() === ${JSON.stringify(project)})`);
  step('Keycloak PKCE sign-in returned to the workspace and listed the project');
  await evaluate(`[...document.querySelectorAll('.projects a')].find(a => a.textContent.trim() === ${JSON.stringify(project)}).click()`);
  await until('new run form', `document.querySelector('form textarea[name=task]') !== null`);
  const before = await evaluate(`document.querySelectorAll('.runs tbody tr').length`);
  await evaluate(`(() => { const set = (s, v) => { const e = document.querySelector(s); e.value = v; e.dispatchEvent(new Event(e.tagName === 'SELECT' ? 'change' : 'input', { bubbles: true })); };
    set('textarea[name=task]', 'Design a small task-list application where users add tasks.');
    set('textarea[name=context]', 'No selected stack. Single user. Keep the design minimal.');
    set('textarea[name=requirements]', 'USER-1: Add tasks');
    set('select[name=workflow]', 'decomposition-v1'); })()`);
  // Zoneless change detection enables the button on the next render, as for a real user.
  await until('enabled submit', `!document.querySelector('form button[type=submit]').disabled`);
  await evaluate(`document.querySelector('form button[type=submit]').click()`);
  await until('queued run', `document.querySelectorAll('.runs tbody tr').length > ${before}`);
  step('a decomposed run was submitted from the form');
  await until('plan approval popup', `document.querySelector('[role=alertdialog] table tbody tr') !== null`, 180000);
  const dialog = await evaluate(`(() => { const d = document.querySelector('[role=alertdialog]');
    return { modal: d.getAttribute('aria-modal'), parts: d.querySelectorAll('tbody tr').length,
      buttons: [...d.querySelectorAll('button')].map(b => b.textContent.trim()) }; })()`);
  if (dialog.modal !== 'true' || dialog.parts < 1 || JSON.stringify(dialog.buttons) !== '["approve","stop"]') throw new Error('unexpected_dialog');
  await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
  await sleep(500);
  if (!(await evaluate(`document.querySelector('[role=alertdialog]') !== null`))) throw new Error('dialog_dismissed_by_escape');
  step(`the blocking popup showed a ${dialog.parts}-part plan with only approve/stop and ignored Escape`);
  await evaluate(`[...document.querySelectorAll('[role=alertdialog] button')].find(b => b.textContent.trim() === 'approve').click()`);
  await until('popup closed', `document.querySelector('[role=alertdialog]') === null`, 30000);
  step('approve was recorded and the popup closed');
  await until('run succeeded', `document.querySelector('.runs tbody tr .status')?.textContent.trim() === 'succeeded'`, 300000);
  step('live updates showed the run as succeeded');
  await evaluate(`document.querySelector('.runs tbody tr a').click()`);
  await until('run detail', `document.querySelectorAll('table tbody tr').length >= 6 && document.body.textContent.includes('Published')`);
  const detail = await evaluate(`({ steps: document.querySelectorAll('table tbody tr').length, tiers: [...document.querySelectorAll('table tbody tr td:nth-child(5)')].map(t => t.textContent.trim()).filter(Boolean) })`);
  step(`run detail: ${detail.steps} steps, routed tiers ${detail.tiers.join('/')}, publication shown`);
  console.log('WORKSPACE_E2E_PASSED');
} catch (error) {
  console.log(['page errors:', ...pageErrors.slice(0, 10)].join(String.fromCharCode(10)));
  try { console.log('page text: ' + String(await evaluate(`document.querySelector('main')?.innerText ?? ''`)).slice(0, 600)); } catch { }
  throw error;
} finally {
  try { socket?.close(); } catch { }
  browser.kill();
  await sleep(500);
  await rm(profile, { recursive: true, force: true }).catch(() => { });
}
