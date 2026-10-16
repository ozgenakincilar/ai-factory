import { DatePipe, DecimalPipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { DevelopmentStatus, DevelopmentSummary, DevelopmentView, FactoryApi } from './factory-api';
import { isApplicationName, seconds } from './verification.page';
import { WorkspaceState } from './workspace.state';

export const activeDevelopment = (status: DevelopmentStatus) => status === 'queued' || status === 'running';

export type DiffLine = { kind: 'file' | 'hunk' | 'add' | 'remove' | 'context'; text: string };

// Classifies unified diff lines for display; the text is shown as-is (Angular escapes it).
export function diffLines(diff: string): DiffLine[] {
  return diff.split('\n').filter((line, i, all) => line.length > 0 || i < all.length - 1).map(text => ({
    text,
    kind: text.startsWith('diff --git') || text.startsWith('+++ ') || text.startsWith('--- ') || text.startsWith('index ') || text.startsWith('new file')
      || text.startsWith('deleted file') ? 'file' : text.startsWith('@@') ? 'hunk' : text.startsWith('+') ? 'add' : text.startsWith('-') ? 'remove' : 'context',
  }));
}

export const usd = (micros: number | null | undefined) => micros == null ? '' : (micros / 1_000_000).toFixed(4);

const pollMs = 3000;

// Shown on an approved review: asks the Developer Agent to implement exactly this version.
@Component({
  selector: 'app-develop-panel',
  imports: [FormsModule],
  template: `
    <section class="card">
      <h2>Develop</h2>
      <p class="meta">The Developer Agent implements this approved version in a generated starter application, builds and tests it
        offline in a sandbox and records the diff as evidence. Each job is capped at USD 0.40.</p>
      <form (ngSubmit)="submit()">
        <label>Application name (PascalCase)
          <input name="developName" [(ngModel)]="name" autocomplete="off" maxlength="64"></label>
        <button type="submit" [disabled]="busy() || !valid()">Develop</button>
      </form>
      @if (message()) { <p role="status">{{ message() }}</p> }
    </section>`,
  styles: `.card { border: 1px solid var(--border); border-radius: 8px; padding: 16px; background: var(--surface); margin-top: 24px; }
    .meta { color: var(--muted); } form { display: flex; gap: 10px; align-items: end; flex-wrap: wrap; }
    label { display: grid; gap: 4px; } input { font: inherit; padding: 6px; border: 1px solid var(--border); border-radius: 6px; background: var(--bg); color: var(--text); }
    button { padding: 8px 16px; border-radius: 6px; border: 0; background: var(--accent); color: white; cursor: pointer; }`,
})
export class DevelopPanel {
  readonly projectId = input.required<string>();
  readonly caseId = input.required<string>();
  readonly version = input.required<number>();
  readonly sha256 = input.required<string>();
  private readonly api = inject(FactoryApi);
  private readonly router = inject(Router);
  protected readonly nameText = signal('');
  protected readonly valid = computed(() => isApplicationName(this.nameText()));
  protected readonly busy = signal(false);
  protected readonly message = signal<string | null>(null);
  private key = crypto.randomUUID();

  protected get name(): string { return this.nameText(); }
  protected set name(value: string) { this.nameText.set(value.trim()); }

  async submit(): Promise<void> {
    if (!this.valid()) return;
    this.busy.set(true); this.message.set(null);
    try {
      const submission = await firstValueFrom(this.api.submitDevelopment(this.projectId(), this.caseId(), this.version(), this.sha256(),
        this.nameText(), this.key));
      await this.router.navigate(['/projects', this.projectId(), 'developments', submission.jobId]);
    } catch (error) {
      const status = error instanceof HttpErrorResponse ? error.status : 0;
      if (status !== 0) this.key = crypto.randomUUID();
      this.message.set(status === 412 ? 'This version is no longer the approved one; reload the review.'
        : status === 429 ? 'Two development jobs are already active in this project.'
        : status === 403 ? 'Only generators of this project can start development.'
        : 'The development job could not be started.');
    } finally { this.busy.set(false); }
  }
}

@Component({
  selector: 'app-developments-panel',
  imports: [RouterLink, DatePipe],
  template: `
    @if (jobs().length) {
      <section class="card">
        <h2>Development jobs</h2>
        <table class="developments">
          <thead><tr><th>Requested</th><th>Application</th><th>Status</th><th>Cost (USD)</th></tr></thead>
          <tbody>
            @for (job of jobs(); track job.jobId) {
              <tr>
                <td><a [routerLink]="['/projects', projectId(), 'developments', job.jobId]">{{ job.createdAt | date: 'medium' }}</a></td>
                <td>{{ job.applicationName }}</td>
                <td><span class="status" [attr.data-status]="job.status">{{ job.status }}</span>{{ job.failureCode ? ' (' + job.failureCode + ')' : '' }}</td>
                <td>{{ cost(job.costMicros) }}</td>
              </tr>
            }
          </tbody>
        </table>
      </section>
    }`,
  styles: `.card { border: 1px solid var(--border); border-radius: 8px; padding: 16px; margin-bottom: 24px; background: var(--surface); }
    table { width: 100%; border-collapse: collapse; } th, td { text-align: left; padding: 6px; border-bottom: 1px solid var(--border); }
    .status[data-status="verified"] { color: var(--ok); } .status[data-status="queued"], .status[data-status="running"] { color: var(--warn); }
    .status:not([data-status="verified"]):not([data-status="queued"]):not([data-status="running"]) { color: var(--danger); }`,
})
export class DevelopmentsPanel implements OnInit {
  readonly projectId = input.required<string>();
  private readonly api = inject(FactoryApi);
  private readonly destroy = inject(DestroyRef);
  protected readonly jobs = signal<DevelopmentSummary[]>([]);
  protected readonly cost = usd;
  private timer: ReturnType<typeof setTimeout> | undefined;

  ngOnInit(): void { this.destroy.onDestroy(() => clearTimeout(this.timer)); void this.load(); }

  async load(): Promise<void> {
    clearTimeout(this.timer);
    try { this.jobs.set(await firstValueFrom(this.api.developments(this.projectId()))); } catch { return; }
    if (this.jobs().some(j => activeDevelopment(j.status))) this.timer = setTimeout(() => void this.load(), pollMs);
  }
}

@Component({
  selector: 'app-development-page',
  imports: [RouterLink, DatePipe, DecimalPipe],
  template: `
    <p><a routerLink="/">Projects</a> / <a [routerLink]="['/projects', id()]">{{ id() }}</a> / development</p>
    @if (view(); as v) {
      <h1>{{ v.job.applicationName }} <span class="status" [attr.data-status]="v.job.status">{{ v.job.status }}</span></h1>
      <p class="meta">Review case <a [routerLink]="['/projects', id(), 'reviews', v.job.caseId]">{{ v.job.caseId }}</a> version {{ v.job.artifactVersion }}
        · requested {{ v.job.createdAt | date: 'medium' }}{{ v.job.failureCode ? ' · ' + v.job.failureCode : '' }}</p>
      @if (v.evidence; as e) {
        <p>{{ e.model }} · {{ e.turns }} turns · {{ e.actions }} actions · estimated USD {{ cost(e.costMicros) }}
          ({{ e.calls.length }} calls, {{ inputTokens() | number }} input / {{ outputTokens() | number }} output tokens)</p>
        <h2>Changes</h2>
        <table class="changes">
          <thead><tr><th>File</th><th>Change</th><th>Requirements</th></tr></thead>
          <tbody>
            @for (c of e.changes; track c.path) { <tr><td><code>{{ c.path }}</code></td><td>{{ c.kind }}</td><td>{{ c.requirementIds.join(', ') }}</td></tr> }
          </tbody>
        </table>
        @if (e.diff) {
          <h2>Diff</h2>
          <pre class="diff">@for (line of lines(); track $index) {<span [class]="line.kind">{{ line.text }}</span>
}</pre>
        }
        @if (e.verification; as report) {
          <h2>Offline verification</h2>
          <table>
            <thead><tr><th>Step</th><th>Outcome</th><th>Exit code</th><th>Duration</th></tr></thead>
            <tbody>
              @for (step of report.steps; track step.name) {
                <tr><td>{{ step.name }}</td><td><span class="status" [attr.data-status]="step.outcome">{{ step.outcome }}</span></td>
                  <td>{{ step.exitCode ?? '' }}</td><td>{{ duration(step.duration) }}</td></tr>
              }
            </tbody>
          </table>
        }
        @if (e.log.length) {
          <details><summary>Agent log</summary><ul>@for (entry of e.log; track $index) { <li>{{ entry }}</li> }</ul></details>
        }
        <h2>Evidence</h2>
        <dl>
          <dt>Evidence SHA-256</dt><dd><code>{{ v.evidenceSha256 }}</code></dd>
          <dt>Approved artifact SHA-256</dt><dd><code>{{ e.artifactSha256 }}</code></dd>
          @if (e.diffSha256) { <dt>Diff SHA-256</dt><dd><code>{{ e.diffSha256 }}</code></dd> }
          @if (e.finalCommit) { <dt>Commits</dt><dd><code>{{ e.baseCommit }}</code> → <code>{{ e.finalCommit }}</code></dd> }
        </dl>
        <p class="meta">The diff is a proposal for human review: the sandbox proves it builds and its tests pass, not that it is correct.</p>
      } @else if (active()) { <p role="status">Waiting for the Developer Agent…</p> }
    } @else if (failed()) { <p class="error" role="alert">The development job could not be loaded.</p> }`,
  styles: `table { width: 100%; border-collapse: collapse; margin-bottom: 12px; } th, td { text-align: left; padding: 6px; border-bottom: 1px solid var(--border); }
    .meta { color: var(--muted); } .error { color: var(--danger); }
    pre.diff { font-size: 0.85em; background: var(--surface); border: 1px solid var(--border); padding: 8px; overflow: auto; max-height: 640px; }
    pre.diff span { display: block; white-space: pre; } .add { color: var(--ok); } .remove { color: var(--danger); } .hunk { color: var(--accent); }
    .file { font-weight: 600; } dl { display: grid; grid-template-columns: max-content 1fr; gap: 4px 12px; } dd { margin: 0; overflow-wrap: anywhere; }
    .status[data-status="verified"], .status[data-status="passed"] { color: var(--ok); }
    .status[data-status="queued"], .status[data-status="running"], .status[data-status="skipped"] { color: var(--warn); }`,
})
export class DevelopmentPage implements OnInit {
  readonly id = input.required<string>();
  readonly jobId = input.required<string>();
  private readonly api = inject(FactoryApi);
  private readonly state = inject(WorkspaceState);
  private readonly destroy = inject(DestroyRef);
  protected readonly view = signal<DevelopmentView | null>(null);
  protected readonly failed = signal(false);
  protected readonly active = computed(() => { const v = this.view(); return v ? activeDevelopment(v.job.status) : false; });
  protected readonly lines = computed(() => diffLines(this.view()?.evidence?.diff ?? ''));
  protected readonly inputTokens = computed(() => this.view()?.evidence?.calls.reduce((s, c) => s + (c.inputTokens ?? 0), 0) ?? 0);
  protected readonly outputTokens = computed(() => this.view()?.evidence?.calls.reduce((s, c) => s + (c.outputTokens ?? 0), 0) ?? 0);
  protected readonly cost = usd;
  protected readonly duration = seconds;
  private timer: ReturnType<typeof setTimeout> | undefined;

  ngOnInit(): void {
    void this.state.open(this.id());
    this.destroy.onDestroy(() => clearTimeout(this.timer));
    void this.load();
  }

  async load(): Promise<void> {
    clearTimeout(this.timer);
    try { this.view.set(await firstValueFrom(this.api.development(this.id(), this.jobId()))); }
    catch { this.failed.set(true); return; }
    if (this.active()) this.timer = setTimeout(() => void this.load(), pollMs);
  }
}
