// M13A.2 acceptance in a real headless Edge, in two human-separated steps:
//   node scripts/Test-FactoryDeployment.mjs <project> <developmentJobId> plan
//       opens the verified development job, requests a deployment to the project's target from the Deploy
//       panel, waits for the plan and prints its changes and binding SHA-256; it never approves.
//   node scripts/Test-FactoryDeployment.mjs <project> <deploymentId> approve <binding sha256>
//       opens the deployment and clicks "Approve and apply" only when the page shows exactly that binding
//       (the human's decision, given in chat); then waits for the verified outcome.
// Requires Keycloak, the API, the web dev server and a worker started with -Verification -Deployment.
// Never prints credentials or tokens.
import { readFile, mkdir, rm } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { resolve } from 'node:path';

const [project, id, mode, approved] = process.argv.slice(2);
if (!project || !id || !['plan', 'approve'].includes(mode) || (mode === 'approve' && !/^[0-9a-f]{64}$/.test(approved ?? '')))
  throw new Error('usage: <project> <id> plan | <project> <deploymentId> approve <binding sha256>');
const app = 'http://127.0.0.1:4200/';
const settings = JSON.parse(await readFile(resolve('artifacts/keycloak/development.json'), 'utf8'));
const profile = resolve('artifacts/keycloak/deployment-browser-profile');
await rm(profile, { recursive: true, force: true });
await mkdir(profile, { recursive: true });
const browser = spawn('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  ['--headless=new', '--disable-gpu', '--no-first-run', '--remote-debugging-address=127.0.0.1', '--remote-debugging-port=58086',
   '--user-data-dir=' + profile, 'about:blank'], { windowsHide: true, stdio: 'ignore' });
const sleep = ms => new Promise(r => setTimeout(r, ms));
let socket, n = 0;
const pending = new Map();
function send(method, params = {}) {
  return new Promise((ok, fail) => { const i = ++n; pending.set(i, { ok, fail }); socket.send(JSON.stringify({ id: i, method, params })); });
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
const button = text => `[...document.querySelectorAll('button')].find(b => b.textContent.trim() === ${JSON.stringify(text)})`;
const status = `document.querySelector('h1 .status')?.textContent.trim()`;
const step = text => console.log('ok  ' + text);

async function report() {
  const page = await evaluate(`({ status: ${status}, meta: document.querySelector('main p.meta')?.textContent.trim(),
    plan: [...document.querySelectorAll('main table')][0] ? [...[...document.querySelectorAll('main table')][0].querySelectorAll('tbody tr')]
      .map(r => r.children[0].textContent.trim() + ' ' + r.children[1].textContent.trim()) : [],
    counts: [...document.querySelectorAll('main p')].map(p => p.textContent.trim()).find(t => t.includes('to add')),
    hashes: [...document.querySelectorAll('dl dt')].map(dt => dt.textContent.trim() + ' ' + dt.nextElementSibling.textContent.trim()),
    url: document.querySelector('.url a')?.getAttribute('href') ?? null,
    checks: [...document.querySelectorAll('main h2')].some(h => h.textContent.trim() === 'Verification')
      ? [...[...document.querySelectorAll('main table')].at(-1).querySelectorAll('tbody tr')].map(r => r.textContent.replace(/\\s+/g, ' ').trim()) : [],
    failure: document.querySelector('details pre')?.textContent.trim().slice(0, 600) ?? null })`);
  console.log('status   ' + page.status);
  if (page.counts) console.log('plan     ' + page.counts);
  for (const line of page.plan) console.log('change   ' + line);
  for (const line of page.hashes) console.log('hash     ' + line);
  if (page.url) console.log('url      ' + page.url);
  for (const line of page.checks) console.log('check    ' + line);
  if (page.failure) console.log('failure  ' + page.failure);
  return page;
}

try {
  let target;
  for (let i = 0; i < 40 && !target; i++) {
    try { target = (await (await fetch('http://127.0.0.1:58086/json/list')).json()).find(t => t.type === 'page'); } catch { await sleep(250); }
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
  await until('project list', `document.querySelector('.projects a') !== null`);
  const kind = mode === 'plan' ? 'developments' : 'deployments';
  await send('Page.navigate', { url: `${app}projects/${encodeURIComponent(project)}/${kind}/${encodeURIComponent(id)}` });

  if (mode === 'plan') {
    await until('deploy panel', `${button('Plan deployment')} && !${button('Plan deployment')}.disabled`);
    step('verified development job opened; the Deploy panel offers ' + await evaluate(`document.querySelector('app-deploy-panel select').value`));
    await evaluate(`${button('Plan deployment')}.click()`);
    await until('deployment page', `location.pathname.includes('/deployments/')`);
    step('deployment requested: ' + await evaluate(`location.pathname.split('/').at(-1)`));
    await until('plan', `['awaitingApproval','failed','expired'].includes(${status})`, 1200000);
    await report();
    console.log('DEPLOYMENT_PLAN_READY (approval is a separate human step)');
  } else {
    await until('plan awaiting approval', `${status} === 'awaitingApproval'`);
    const shown = await evaluate(`[...document.querySelectorAll('dl dt')].find(dt => dt.textContent.trim() === 'Binding SHA-256').nextElementSibling.textContent.trim()`);
    if (shown !== approved) throw new Error('binding_differs_from_the_approved_one');
    step('the page shows exactly the approved binding');
    await evaluate(`${button('Approve and apply')}.click()`);
    await until('outcome', `['succeeded','unhealthy','failed'].includes(${status})`, 3600000);
    const page = await report();
    console.log(page.status === 'succeeded' ? 'DEPLOYMENT_E2E_SUCCEEDED' : 'DEPLOYMENT_E2E_NOT_SUCCEEDED');
  }
} catch (error) {
  try { console.log('page text: ' + String(await evaluate(`document.querySelector('main')?.innerText ?? ''`)).slice(0, 1200)); } catch { }
  throw error;
} finally {
  try { socket?.close(); } catch { }
  browser.kill();
  await sleep(500);
  await rm(profile, { recursive: true, force: true }).catch(() => { });
}
