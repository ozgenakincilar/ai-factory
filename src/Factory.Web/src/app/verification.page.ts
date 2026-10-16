import { DatePipe } from '@angular/common';
import { Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { FactoryApi, VerificationStatus, VerificationSummary, VerificationView } from './factory-api';
import { WorkspaceState } from './workspace.state';

// Same rule as the Factory's ApplicationName: PascalCase, 2 to 6 words, ASCII letters and digits.
export function isApplicationName(value: string): boolean {
  return /^[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]+){0,5}$/.test(value);
}

export const activeVerification = (status: VerificationStatus) => status === 'queued' || status === 'running';

// "00:01:02.5000000" (TimeSpan) → "62.5 s".
export function seconds(duration: string): string {
  const match = /^(?:(\d+)\.)?(\d+):(\d+):(\d+(?:\.\d+)?)$/.exec(duration);
  if (!match) return duration;
  const total = (+(match[1] ?? 0)) * 86400 + +match[2] * 3600 + +match[3] * 60 + +match[4];
  return `${Math.round(total * 10) / 10} s`;
}

// Status changes of verification jobs are not pushed; active jobs are re-read on this interval.
const pollMs = 3000;

@Component({
  selector: 'app-verifications-panel',
  imports: [FormsModule, RouterLink, DatePipe],
  template: `
    <section class="card">
      <h2>Starter application verification</h2>
      <p class="meta">Generates an application from the starter template (Angular + ASP.NET Core + PostgreSQL, DDD layers) and builds
        and tests it offline in an isolated sandbox.</p>
      <form (ngSubmit)="submit()">
        <label>Application name (PascalCase, for example StockFlow)
          <input name="applicationName" [(ngModel)]="name" autocomplete="off" maxlength="64"></label>
        <button type="submit" [disabled]="busy() || !valid()">Verify</button>
        @if (message()) { <span role="status">{{ message() }}</span> }
      </form>
      @if (jobs().length) {
        <table class="verifications">
          <thead><tr><th>Requested</th><th>Application</th><th>Status</th><th>Attempts</th></tr></thead>
          <tbody>
            @for (job of jobs(); track job.jobId) {
              <tr>
                <td><a [routerLink]="['/projects', projectId(), 'verifications', job.jobId]">{{ job.createdAt | date: 'medium' }}</a></td>
                <td>{{ job.applicationName }}</td>
                <td><span class="status" [attr.data-status]="job.status">{{ job.status }}</span>{{ job.failureCode ? ' (' + job.failureCode + ')' : '' }}</td>
                <td>{{ job.attempts }}</td>
              </tr>
            }
          </tbody>
        </table>
      }
    </section>`,
  styles: `.card { border: 1px solid var(--border); border-radius: 8px; padding: 16px; margin-bottom: 24px; background: var(--surface); }
    .meta { color: var(--muted); } form { display: flex; gap: 10px; align-items: end; flex-wrap: wrap; margin-bottom: 12px; }
    label { display: grid; gap: 4px; } input { font: inherit; padding: 6px; border: 1px solid var(--border); border-radius: 6px; background: var(--bg); color: var(--text); }
    button { padding: 8px 16px; border-radius: 6px; border: 0; background: var(--accent); color: white; cursor: pointer; }
    table { width: 100%; border-collapse: collapse; } th, td { text-align: left; padding: 6px; border-bottom: 1px solid var(--border); }
    .status[data-status="passed"] { color: var(--ok); } .status[data-status="failed"], .status[data-status="timedOut"], .status[data-status="sandboxError"] { color: var(--danger); }
    .status[data-status="running"], .status[data-status="queued"] { color: var(--warn); }`,
})
export class VerificationsPanel implements OnInit {
  readonly projectId = input.required<string>();
  private readonly api = inject(FactoryApi);
  private readonly destroy = inject(DestroyRef);
  protected readonly jobs = signal<VerificationSummary[]>([]);
  protected readonly busy = signal(false);
  protected readonly message = signal<string | null>(null);
  protected readonly nameText = signal('');
  protected readonly valid = computed(() => isApplicationName(this.nameText()));
  private timer: ReturnType<typeof setTimeout> | undefined;

  protected get name(): string { return this.nameText(); }
  protected set name(value: string) { this.nameText.set(value.trim()); }

  ngOnInit(): void {
    this.destroy.onDestroy(() => clearTimeout(this.timer));
    void this.load();
  }

  async load(): Promise<void> {
    clearTimeout(this.timer);
    try { this.jobs.set(await firstValueFrom(this.api.verifications(this.projectId()))); }
    catch { this.message.set('Verifications could not be loaded.'); }
    if (this.jobs().some(j => activeVerification(j.status))) this.timer = setTimeout(() => void this.load(), pollMs);
  }

  async submit(): Promise<void> {
    if (!this.valid()) return;
    this.busy.set(true); this.message.set(null);
    try {
      await firstValueFrom(this.api.submitVerification(this.projectId(), this.nameText(), crypto.randomUUID()));
      this.message.set('Verification queued.');
      await this.load();
    } catch { this.message.set('The verification could not be started.'); }
    finally { this.busy.set(false); }
  }
}

@Component({
  selector: 'app-verification-page',
  imports: [RouterLink, DatePipe],
  template: `
    <p><a routerLink="/">Projects</a> / <a [routerLink]="['/projects', id()]">{{ id() }}</a> / verification</p>
    @if (view(); as v) {
      <h1>{{ v.job.applicationName }} <span class="status" [attr.data-status]="v.job.status">{{ v.job.status }}</span></h1>
      <p class="meta">{{ v.job.templateId }} {{ v.job.templateVersion }} · requested {{ v.job.createdAt | date: 'medium' }} · attempt {{ v.job.attempts }}
        {{ v.job.failureCode ? ' · ' + v.job.failureCode : '' }}</p>
      @if (v.evidence; as e) {
        <h2>Sandbox steps</h2>
        <table>
          <thead><tr><th>Step</th><th>Outcome</th><th>Exit code</th><th>Duration</th></tr></thead>
          <tbody>
            @for (step of e.report.steps; track step.name) {
              <tr><td>{{ step.name }}</td><td><span class="status" [attr.data-status]="step.outcome">{{ step.outcome }}</span></td>
                <td>{{ step.exitCode ?? '' }}</td><td>{{ duration(step.duration) }}</td></tr>
            }
          </tbody>
        </table>
        @for (step of e.report.steps; track step.name) {
          @if (step.output) {
            <details><summary>{{ step.name }} output{{ step.outputTruncated ? ' (truncated)' : '' }}</summary><pre>{{ step.output }}</pre></details>
          }
        }
        <h2>Evidence</h2>
        <dl>
          <dt>Evidence SHA-256</dt><dd><code>{{ v.evidenceSha256 }}</code></dd>
          <dt>Runner image</dt><dd><code>{{ e.report.runnerImage }}</code></dd>
          <dt>Template SHA-256</dt><dd><code>{{ e.templateSha256 }}</code></dd>
          <dt>Generated workspace SHA-256</dt><dd><code>{{ e.manifestSha256 }}</code></dd>
          <dt>Limits</dt><dd>{{ e.report.limits.cpus }} CPUs · {{ e.report.limits.memoryMb }} MiB memory · {{ e.report.limits.pids }} processes ·
            {{ e.report.limits.workspaceMb }} MiB workspace · no network</dd>
          <dt>Total duration</dt><dd>{{ duration(e.report.duration) }}</dd>
        </dl>
        <p class="meta">The sandbox isolates the generated code; passing tests are the code's own claims, not an independent review.</p>
      } @else if (active()) { <p role="status">Waiting for a worker to finish the verification…</p> }
    } @else if (failed()) { <p class="error" role="alert">The verification could not be loaded.</p> }`,
  styles: `table { width: 100%; border-collapse: collapse; margin-bottom: 12px; } th, td { text-align: left; padding: 6px; border-bottom: 1px solid var(--border); }
    .meta { color: var(--muted); } .error { color: var(--danger); }
    pre { white-space: pre-wrap; word-break: break-all; max-height: 320px; overflow: auto; background: var(--surface); padding: 8px; border: 1px solid var(--border); }
    dl { display: grid; grid-template-columns: max-content 1fr; gap: 4px 12px; } dd { margin: 0; overflow-wrap: anywhere; }
    .status[data-status="passed"] { color: var(--ok); } .status[data-status="failed"], .status[data-status="timedOut"], .status[data-status="sandboxError"] { color: var(--danger); }
    .status[data-status="running"], .status[data-status="queued"], .status[data-status="skipped"] { color: var(--warn); }`,
})
export class VerificationPage implements OnInit {
  readonly id = input.required<string>();
  readonly jobId = input.required<string>();
  private readonly api = inject(FactoryApi);
  private readonly state = inject(WorkspaceState);
  private readonly destroy = inject(DestroyRef);
  protected readonly view = signal<VerificationView | null>(null);
  protected readonly failed = signal(false);
  protected readonly active = computed(() => { const v = this.view(); return v ? activeVerification(v.job.status) : false; });
  protected readonly duration = seconds;
  private timer: ReturnType<typeof setTimeout> | undefined;

  ngOnInit(): void {
    void this.state.open(this.id());
    this.destroy.onDestroy(() => clearTimeout(this.timer));
    void this.load();
  }

  async load(): Promise<void> {
    clearTimeout(this.timer);
    try { this.view.set(await firstValueFrom(this.api.verification(this.id(), this.jobId()))); }
    catch { this.failed.set(true); return; }
    if (this.active()) this.timer = setTimeout(() => void this.load(), pollMs);
  }
}
