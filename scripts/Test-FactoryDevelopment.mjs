// M10.3 acceptance in a real headless Edge: a StockFlow Product CRUD request goes through analysis
// and architecture (worker model), is approved on the review page, and the Developer Agent implements
// it from the "Develop" panel (gpt-5.4-mini, USD 0.40 job cap). Reports the outcome; it does not
// assume success. Requires Keycloak, the API, the web dev server and a worker started with
// -Verification -Development. Never prints credentials or tokens.
// Usage: node scripts/Test-FactoryDevelopment.mjs [project] [ui] [approved review case id]
//   ui (M14): also asks for the product management screen in the Angular web app (template 1.1.0 serves it).
import { readFile, mkdir, rm } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { resolve } from 'node:path';

const project = process.argv[2] ?? 'factory-workspace';
const withUi = process.argv[3] === 'ui';
const existingCase = process.argv[4];
const app = 'http://127.0.0.1:4200/';
const settings = JSON.parse(await readFile(resolve('artifacts/keycloak/development.json'), 'utf8'));
const profile = resolve('artifacts/keycloak/development-browser-profile');
await rm(profile, { recursive: true, force: true });
await mkdir(profile, { recursive: true });
const browser = spawn('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  ['--headless=new', '--disable-gpu', '--no-first-run', '--remote-debugging-address=127.0.0.1', '--remote-debugging-port=58085',
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
const setValue = (selector, value) => `(() => { const e = document.querySelector(${JSON.stringify(selector)}); e.value = ${JSON.stringify(value)};
  e.dispatchEvent(new Event(e.tagName === 'SELECT' ? 'change' : 'input', { bubbles: true })); })()`;
const button = text => `[...document.querySelectorAll('button')].find(b => b.textContent.trim() === ${JSON.stringify(text)})`;
const step = text => console.log('ok  ' + text);

const task = 'StockFlow: manage the product catalog. Users create, list, view, update and delete products.';
const context = 'Use the generated starter application (ASP.NET Core + PostgreSQL + Angular, DDD layers). Business rules belong in the '
  + 'Product aggregate; the application layer exposes use cases through a repository port; Infrastructure persists with EF Core; '
  + 'the Api exposes /api/products. No new packages are available.'
  + (withUi ? ' The Angular app in web/ calls /api/products on the same origin with HttpClient (already provided) and has a products '
    + 'screen as its start page; keep its unit tests passing.' : '');
const requirements = [
  'USER-1: A product has a SKU that is required, unique, at most 32 characters of uppercase letters, digits or hyphens',
  'USER-2: A product has a name that is required and at most 100 characters',
  'USER-3: A product has a price that must not be negative',
  'USER-4: Users can create, list, view, update and delete products',
  ...(withUi ? ['USER-5: The web app lists products and lets users create, edit and delete them'] : []),
].join('\n');

try {
  let target;
  for (let i = 0; i < 40 && !target; i++) {
    try { target = (await (await fetch('http://127.0.0.1:58085/json/list')).json()).find(t => t.type === 'page'); } catch { await sleep(250); }
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
  if (existingCase) {
    // Develops an already approved review again (no new analysis run).
    await send('Page.navigate', { url: `${app}projects/${encodeURIComponent(project)}/reviews/${encodeURIComponent(existingCase)}` });
    step('approved review ' + existingCase + ' opened');
  } else {
  await evaluate(`[...document.querySelectorAll('.projects a')].find(a => a.textContent.trim() === ${JSON.stringify(project)}).click()`);
  await until('new run form', `document.querySelector('form textarea[name=task]') !== null`);
  const before = await evaluate(`document.querySelectorAll('.runs tbody tr').length`);
  await evaluate(setValue('textarea[name=task]', task));
  await evaluate(setValue('textarea[name=context]', context));
  await evaluate(setValue('textarea[name=requirements]', requirements));
  await evaluate(setValue('select[name=workflow]', 'analysis-architecture-v1'));
  await until('enabled submit', `!document.querySelector('form button[type=submit]').disabled`);
  await evaluate(`document.querySelector('form button[type=submit]').click()`);
  await until('queued run', `document.querySelectorAll('.runs tbody tr').length > ${before}`);
  step('StockFlow Product CRUD request submitted');
  await until('run succeeded', `['succeeded','failed','cancelled'].includes(document.querySelector('.runs tbody tr .status')?.textContent.trim())`, 300000);
  if (await evaluate(`document.querySelector('.runs tbody tr .status').textContent.trim()`) !== 'succeeded') throw new Error('analysis_run_failed');
  await evaluate(`document.querySelector('.runs tbody tr a').click()`);
  await until('review link', `document.querySelector('a.review') !== null`);
  await evaluate(`document.querySelector('a.review').click()`);
  await until('verified review', `document.body.textContent.includes('content verified') && ${button('Approve')} && !${button('Approve')}.disabled`);
  const requirementCount = await evaluate(`document.querySelectorAll('.requirements tbody tr').length`);
  step(`analysis and architecture published; review shows ${requirementCount} requirements, content verified`);
  await evaluate(`${button('Approve')}.click()`);
  }
  await until('develop panel', `document.querySelector('app-develop-panel input[name=developName]') !== null`);
  step('review approved; the Develop panel is offered for the approved version');
  await evaluate(setValue('app-develop-panel input[name=developName]', 'StockFlow'));
  await until('enabled develop', `!document.querySelector('app-develop-panel button[type=submit]').disabled`);
  await evaluate(`document.querySelector('app-develop-panel button[type=submit]').click()`);
  await until('development page', `location.pathname.includes('/developments/')`);
  step('development job submitted');
  const terminal = `['verified','verificationFailed','notFinished','budgetExhausted','modelFailed','sandboxFailed','failed']`;
  await until('development finished', `${terminal}.includes(document.querySelector('h1 .status')?.textContent.trim())`, 1500000);
  await until('evidence', `document.body.textContent.includes('Evidence SHA-256') || document.querySelector('h1 .status').textContent.trim() === 'failed'`, 30000);
  const result = await evaluate(`({ status: document.querySelector('h1 .status').textContent.trim(), summary: document.querySelector('main p:nth-of-type(3)')?.textContent.trim(),
    changes: [...document.querySelectorAll('.changes tbody tr')].map(r => r.children[0].textContent.trim() + ' [' + r.children[2].textContent.trim() + ']'),
    steps: [...document.querySelectorAll('table:not(.changes) tbody tr')].map(r => r.children[0].textContent.trim() + '=' + r.children[1].textContent.trim()) })`);
  console.log('status  ' + result.status);
  console.log('summary ' + result.summary);
  for (const change of result.changes) console.log('change  ' + change);
  console.log('steps   ' + result.steps.join(', '));
  console.log(result.status === 'verified' ? 'DEVELOPMENT_E2E_VERIFIED' : 'DEVELOPMENT_E2E_NOT_VERIFIED');
} catch (error) {
  try { console.log('page text: ' + String(await evaluate(`document.querySelector('main')?.innerText ?? ''`)).slice(0, 1200)); } catch { }
  throw error;
} finally {
  try { socket?.close(); } catch { }
  browser.kill();
  await sleep(500);
  await rm(profile, { recursive: true, force: true }).catch(() => { });
}
