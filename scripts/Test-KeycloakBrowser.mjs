// Local synthetic-user smoke test using a real headless Edge browser, PKCE, state and nonce.
// Requires the operator-provisioned development realm. Never prints credentials or tokens.
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { randomBytes, createHash } from 'node:crypto';
import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { resolve } from 'node:path';
import { createServer as createPortProbe } from 'node:net';

const local = resolve('artifacts/keycloak');
const settings = JSON.parse(await readFile(resolve(local, 'development.json'), 'utf8'));
const issuer = 'http://127.0.0.1:58080/realms/factory-dev';
const redirect = 'http://127.0.0.1:58081/callback';
const verifier = randomBytes(32).toString('base64url');
const challenge = createHash('sha256').update(verifier).digest('base64url');
const state = randomBytes(32).toString('base64url');
const nonce = randomBytes(32).toString('base64url');
// Refuse to attach to a pre-existing debugging service/browser.
const probe = createPortProbe();
await new Promise((resolve,reject)=>{probe.once('error',reject);probe.listen(58082,'127.0.0.1',resolve);});
await new Promise(resolve=>probe.close(resolve));
let callbackResolve;
const callback = new Promise(resolve => { callbackResolve = resolve; });
const server = createServer((req, res) => {
    const url = new URL(req.url, redirect);
    if (url.pathname !== '/callback' || url.searchParams.get('state') !== state || !url.searchParams.has('code')) {
        res.writeHead(400).end('Invalid callback'); return;
    }
    res.writeHead(200, {'Content-Type':'text/plain', 'Cache-Control':'no-store'}).end('Factory local authentication verification completed.');
    callbackResolve(url.searchParams.get('code'));
});
await new Promise((resolve, reject) => { server.once('error', reject); server.listen(58081, '127.0.0.1', resolve); });
await mkdir(resolve(local, 'browser-profile'), { recursive: true });
const browser = spawn('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    ['--headless=new','--disable-gpu','--no-first-run','--remote-debugging-address=127.0.0.1','--remote-debugging-port=58082',
     '--user-data-dir='+resolve(local,'browser-profile'),'about:blank'], { windowsHide:true, stdio:'ignore' });
let socket;
let callbackTimer;
try {
    let target;
    for (let n=0;n<100;n++) {
        try { const targets=await (await fetch('http://127.0.0.1:58082/json/list')).json(); target=targets.find(x=>x.type==='page'); if(target)break; } catch {}
        await new Promise(r=>setTimeout(r,200));
    }
    if(!target)throw Error('browser_debug_endpoint_unavailable');
    socket=new WebSocket(target.webSocketDebuggerUrl);
    await new Promise((resolve,reject)=>{socket.onopen=resolve;socket.onerror=reject;});
    let sequence=0;const pending=new Map();
    socket.onmessage=({data})=>{const message=JSON.parse(data);if(pending.has(message.id)){pending.get(message.id)(message);pending.delete(message.id);}};
    const command=(method,params={})=>new Promise(resolve=>{const id=++sequence;pending.set(id,resolve);socket.send(JSON.stringify({id,method,params}));});
    const parameters=new URLSearchParams({client_id:'factory-dev-cli',response_type:'code',scope:'openid',redirect_uri:redirect,
        code_challenge:challenge,code_challenge_method:'S256',state,nonce,prompt:'login'});
    await command('Page.navigate',{url:issuer+'/protocol/openid-connect/auth?'+parameters});
    let ready=false;
    for(let n=0;n<100;n++) {
        const response=await command('Runtime.evaluate',{expression:'Boolean(document.querySelector("#username") && document.querySelector("#password"))',returnByValue:true});
        if(response.result?.result?.value){ready=true;break;}
        await new Promise(r=>setTimeout(r,200));
    }
    if(!ready)throw Error('login_form_unavailable');
    await command('Runtime.evaluate',{expression:
        'document.querySelector("#username").value="factory-reviewer";document.querySelector("#password").value='+JSON.stringify(settings.HumanPassword)+';document.querySelector("#kc-login").click();'});
    const code=await Promise.race([callback,new Promise((_,reject)=>{callbackTimer=setTimeout(()=>reject(Error('browser_callback_timeout')),30000);})]);
    clearTimeout(callbackTimer);
    const response=await fetch(issuer+'/protocol/openid-connect/token',{method:'POST',body:new URLSearchParams({
        grant_type:'authorization_code',client_id:'factory-dev-cli',redirect_uri:redirect,code,code_verifier:verifier})});
    if(!response.ok)throw Error('code_exchange_failed');
    const tokens=await response.json();
    // Signature/lifetime/audience and nonce are independently checked by the live .NET test.
    await writeFile(resolve(local,'human-tokens.json'),JSON.stringify({accessToken:tokens.access_token,idToken:tokens.id_token,nonce}),{mode:0o600});
    console.log('Real headless Edge login, state validation and PKCE code exchange succeeded. Tokens retained only in ignored local evidence for .NET validation.');
    await command('Browser.close');
} catch {
    console.error('Browser authentication verification failed; no credential, URL query or token details printed.');
    process.exitCode=1;
} finally {
    clearTimeout(callbackTimer); socket?.close(); server.close(); browser.kill();
}
