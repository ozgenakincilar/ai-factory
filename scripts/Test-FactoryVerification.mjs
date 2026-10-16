// End-to-end check of M9.3 in a real headless Edge: Keycloak sign-in, a starter verification
// submitted from the project page, the worker's offline sandbox run (Run-FactoryWorker.ps1
// -Verification), and the evidence on the verification page. No model calls are involved.
// Requires Keycloak, the API (Run-FactoryWorkspace.ps1), the web dev server, Docker and a worker.
// Never prints credentials or tokens. Usage: node scripts/Test-FactoryVerification.mjs [project] [ApplicationName]
import { readFile, mkdir, rm } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { resolve } from 'node:path';

const project = process.argv[2] ?? 'factory-workspace';
const application = process.argv[3] ?? 'BrowserShop';
const app = 'http://127.0.0.1:4200/';
const settings = JSON.parse(await readFile(resolve('artifacts/keycloak/development.json'), 'utf8'));
const profile = resolve('artifacts/keycloak/verification-browser-profile');
await rm(profile, { recursive: true, force: true });
await mkdir(profile, { recursive: true });
const browser = spawn('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  ['--headless=new', '--disable-gpu', '--no-first-run', '--remote-debugging-address=127.0.0.1', '--remote-debugging-port=58084',
   '--user-data-dir=' + profile, 'about:blank'], { windowsHide: true, stdio: 'ignore' });
const sleep = ms => new Promise(r => setTimeout(r, ms));
let socket, id = 0;
const pending = new Map();
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
  while (Date.now() < end) { try { if (await evaluate(expression)) return; } catch { /* page navigating */ } await sleep(500); }
  throw new Error('timeout: ' + description);
}
const step = text => console.log('ok  ' + text);
try {
  let target;
  for (let i = 0; i < 40 && !target; i++) {
    try { target = (await (await fetch('http://127.0.0.1:58084/json/list')).json()).find(t => t.type === 'page'); } catch { await sleep(250); }
  }
  socket = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((ok, fail) => { socket.onopen = ok; socket.onerror = fail; });
  socket.onmessage = event => {
    const message = JSON.parse(event.data);
    if (message.id && pending.has(message.id)) { const p = pending.get(message.id); pending.delete(message.id); message.error ? p.fail(new Error(message.error.message)) : p.ok(message.result); }
  };
  await send('Page.enable'); await send('Runtime.enable');
  await send('Page.navigate', { url: app });
  await until('sign-in page', `document.querySelector('.welcome button') !== null`);
  await evaluate(`document.querySelector('.welcome button').click()`);
  await until('Keycloak login form', `location.host === '127.0.0.1:58080' && document.querySelector('#username') !== null`);
  await evaluate(`(() => { const set = (s, v) => { const e = document.querySelector(s); e.value = v; e.dispatchEvent(new Event('input', { bubbles: true })); };
    set('#username', ${JSON.stringify('factory-reviewer')}); set('#password', ${JSON.stringify(settings.HumanPassword)});
    document.querySelector('#kc-login').click(); })()`);
  await until('project list', `[...document.querySelectorAll('.projects a')].some(a => a.textContent.trim() === ${JSON.stringify(project)})`);
  await evaluate(`[...document.querySelectorAll('.projects a')].find(a => a.textContent.trim() === ${JSON.stringify(project)}).click()`);
  await until('verification form', `document.querySelector('app-verifications-panel input[name=applicationName]') !== null`);
  step('signed in and opened the project with the verification panel');
  const before = await evaluate(`document.querySelectorAll('.verifications tbody tr').length`);
  await evaluate(`(() => { const e = document.querySelector('app-verifications-panel input[name=applicationName]');
    e.value = ${JSON.stringify(application)}; e.dispatchEvent(new Event('input', { bubbles: true })); })()`);
  await until('enabled verify button', `!document.querySelector('app-verifications-panel button[type=submit]').disabled`);
  await evaluate(`document.querySelector('app-verifications-panel button[type=submit]').click()`);
  await until('queued verification', `document.querySelectorAll('.verifications tbody tr').length > ${before}`);
  step(`verification of ${application} submitted`);
  await until('finished verification', `['passed','failed','timedOut','sandboxError'].includes(document.querySelector('.verifications tbody tr .status')?.textContent.trim())`, 600000);
  const status = await evaluate(`document.querySelector('.verifications tbody tr .status').textContent.trim()`);
  if (status !== 'passed') throw new Error('verification_' + status);
  step('the worker finished the sandbox run and the list updated to passed');
  await evaluate(`document.querySelector('.verifications tbody tr a').click()`);
  await until('evidence', `document.body.textContent.includes('Evidence SHA-256') && document.querySelectorAll('table tbody tr').length >= 7`);
  const detail = await evaluate(`({ steps: [...document.querySelectorAll('table tbody tr')].map(r => r.children[0].textContent.trim() + '=' + r.children[1].textContent.trim()),
    hash: document.querySelector('dd code')?.textContent.trim() })`);
  if (detail.steps.length !== 7 || detail.steps.some(s => !s.endsWith('=passed')) || !/^[0-9a-f]{64}$/.test(detail.hash ?? '')) throw new Error('unexpected_evidence');
  step(`evidence page: ${detail.steps.join(', ')}; evidence SHA-256 ${detail.hash.slice(0, 12)}…`);
  console.log('VERIFICATION_E2E_PASSED');
} catch (error) {
  try { console.log('page text: ' + String(await evaluate(`document.querySelector('main')?.innerText ?? ''`)).slice(0, 800)); } catch { }
  throw error;
} finally {
  try { socket?.close(); } catch { }
  browser.kill();
  await sleep(500);
  await rm(profile, { recursive: true, force: true }).catch(() => { });
}
