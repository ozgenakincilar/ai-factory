import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { DeploymentStatus, DeploymentTarget, DeploymentView, FactoryApi } from './factory-api';
import { WorkspaceState } from './workspace.state';

export const activeDeployment = (status: DeploymentStatus) => status === 'queued' || status === 'planning' || status === 'approved' || status === 'applying';

// Only HTTPS addresses of Azure App Service apps are rendered as links.
export const safeAppUrl = (url: string | null | undefined): boolean => !!url && /^https:\/\/[a-z0-9-]{1,60}\.azurewebsites\.net$/.test(url);

const pollMs = 4000;

// Shown on a verified development job: plans its release for one of the project's targets.
@Component({
  selector: 'app-deploy-panel',
  imports: [FormsModule],
  template: `
    <section class="card">
      <h2>Deploy</h2>
      @if (targets().length) {
        <p class="meta">The Factory builds this verified release and plans the infrastructure change. Nothing changes in the cloud until an
          organization owner or admin approves that exact plan.</p>
        <form (ngSubmit)="submit()">
          <label>Target
            <select name="target" [(ngModel)]="target">
              @for (t of targets(); track t.name) { <option [value]="t.name">{{ t.name }} ({{ t.environment }}, {{ t.location }})</option> }
            </select></label>
          <button type="submit" [disabled]="busy() || !target">Plan deployment</button>
        </form>
      } @else { <p class="meta">No deployment target is registered for this project.</p> }
      @if (message()) { <p role="status">{{ message() }}</p> }
    </section>`,
  styles: `.card { border: 1px solid var(--border); border-radius: 8px; padding: 16px; background: var(--surface); margin-top: 24px; }
    .meta { color: var(--muted); } form { display: flex; gap: 10px; align-items: end; flex-wrap: wrap; } label { display: grid; gap: 4px; }
    select { font: inherit; padding: 6px; border: 1px solid var(--border); border-radius: 6px; background: var(--bg); color: var(--text); }
    button { padding: 8px 16px; border-radius: 6px; border: 0; background: var(--accent); color: white; cursor: pointer; }`,
})
export class DeployPanel implements OnInit {
  readonly projectId = input.required<string>();
  readonly jobId = input.required<string>();
  private readonly api = inject(FactoryApi);
  private readonly router = inject(Router);
  protected readonly targets = signal<DeploymentTarget[]>([]);
  protected readonly busy = signal(false);
  protected readonly message = signal<string | null>(null);
  protected target = '';
  private key = crypto.randomUUID();

  async ngOnInit(): Promise<void> {
    try {
      const list = await firstValueFrom(this.api.deployments(this.projectId()));
      this.targets.set(list.targets);
      this.target = list.targets[0]?.name ?? '';
    } catch { this.message.set('Deployment targets could not be loaded.'); }
  }

  async submit(): Promise<void> {
    if (!this.target) return;
    this.busy.set(true); this.message.set(null);
    try {
      const submission = await firstValueFrom(this.api.submitDeployment(this.projectId(), this.target, this.jobId(), this.key));
      await this.router.navigate(['/projects', this.projectId(), 'deployments', submission.deploymentId]);
    } catch (error) {
      const status = error instanceof HttpErrorResponse ? error.status : 0;
      if (status !== 0) this.key = crypto.randomUUID();
      this.message.set(status === 429 ? 'Another deployment to this target is still in progress.'
        : status === 412 ? 'Only verified development jobs can be deployed.'
        : status === 403 ? 'Only generators of this project can request deployments.'
        : 'The deployment could not be requested.');
    } finally { this.busy.set(false); }
  }
}

// The project's deployments, newest first; links to each deployment's plan, decision and outcome.
@Component({
  selector: 'app-deployments-panel',
  imports: [RouterLink, DatePipe],
  template: `
    @if (deployments().length) {
      <section class="card">
        <h2>Deployments</h2>
        <table class="deployments">
          <thead><tr><th>Requested</th><th>Target</th><th>Status</th><th>Plan</th></tr></thead>
          <tbody>
            @for (d of deployments(); track d.deploymentId) {
              <tr>
                <td><a [routerLink]="['/projects', projectId(), 'deployments', d.deploymentId]">{{ d.createdAt | date: 'medium' }}</a></td>
                <td>{{ d.targetName }}</td>
                <td><span class="status" [attr.data-status]="d.status">{{ d.status }}</span>{{ d.failureCode ? ' (' + d.failureCode + ')' : '' }}</td>
                <td>{{ d.summary ? d.summary.add + ' add · ' + d.summary.change + ' change · ' + d.summary.destroy + ' destroy' : '' }}</td>
              </tr>
            }
          </tbody>
        </table>
      </section>
    }`,
  styles: `.card { border: 1px solid var(--border); border-radius: 8px; padding: 16px; margin-bottom: 24px; background: var(--surface); }
    table { width: 100%; border-collapse: collapse; } th, td { text-align: left; padding: 6px; border-bottom: 1px solid var(--border); }
    .status[data-status="succeeded"] { color: var(--ok); }
    .status[data-status="queued"], .status[data-status="planning"], .status[data-status="awaitingApproval"], .status[data-status="approved"],
    .status[data-status="applying"] { color: var(--warn); }
    .status[data-status="failed"], .status[data-status="unhealthy"], .status[data-status="rejected"], .status[data-status="expired"] { color: var(--danger); }`,
})
export class DeploymentsPanel implements OnInit {
  readonly projectId = input.required<string>();
  private readonly api = inject(FactoryApi);
  private readonly destroy = inject(DestroyRef);
  protected readonly deployments = signal<DeploymentView[]>([]);
  private timer: ReturnType<typeof setTimeout> | undefined;

  ngOnInit(): void { this.destroy.onDestroy(() => clearTimeout(this.timer)); void this.load(); }

  async load(): Promise<void> {
    clearTimeout(this.timer);
    try { this.deployments.set((await firstValueFrom(this.api.deployments(this.projectId()))).deployments); } catch { return; }
    if (this.deployments().some(d => activeDeployment(d.status))) this.timer = setTimeout(() => void this.load(), pollMs);
  }
}

@Component({
  selector: 'app-deployment-page',
  imports: [RouterLink, DatePipe, FormsModule],
  template: `
    <p><a routerLink="/">Projects</a> / <a [routerLink]="['/projects', id()]">{{ id() }}</a> / deployment</p>
    @if (view(); as d) {
      <h1>{{ d.targetName }} <span class="status" [attr.data-status]="d.status">{{ d.status }}</span></h1>
      <p class="meta">Release of development job <a [routerLink]="['/projects', id(), 'developments', d.developmentJobId]">{{ d.developmentJobId }}</a>
        · requested {{ d.createdAt | date: 'medium' }}{{ d.failureCode ? ' · ' + d.failureCode : '' }}</p>
      @if (d.url && safeUrl(d.url)) {
        <p class="url">Deployed at <a [href]="d.url" target="_blank" rel="noopener noreferrer">{{ d.url }}</a></p>
      }
      @if (d.summary; as s) {
        <h2>Plan</h2>
        <p>{{ s.add }} to add · {{ s.change }} to change · {{ s.destroy }} to destroy · module {{ d.moduleId }} {{ d.moduleVersion }}
          · planned {{ d.plannedAt | date: 'medium' }}</p>
        @if (d.destructive) { <p class="error" role="alert">This plan deletes or replaces resources.</p> }
        <table>
          <thead><tr><th>Action</th><th>Resource</th></tr></thead>
          <tbody>@for (c of s.changes; track c.address) { <tr><td>{{ c.actions.join(' + ') }}</td><td><code>{{ c.address }}</code></td></tr> }</tbody>
        </table>
        <dl>
          <dt>Binding SHA-256</dt><dd><code>{{ d.bindingSha256 }}</code></dd>
          <dt>Release SHA-256</dt><dd><code>{{ d.releaseSha256 }}</code></dd>
          @if (d.planSha256) { <dt>Plan SHA-256</dt><dd><code>{{ d.planSha256 }}</code></dd> }
        </dl>
      }
      @if (d.status === 'awaitingApproval') {
        <section class="card decision">
          <h2>Decision</h2>
          <p class="meta">Approving applies exactly this plan (binding above) to the target. The approval is valid for 24 hours after planning.</p>
          @if (d.destructive) {
            <label class="check"><input type="checkbox" name="destructive" [(ngModel)]="allowDestructive"> I accept that resources are deleted or replaced</label>
          }
          <div class="actions">
            <button type="button" (click)="decide(true)" [disabled]="busy() || (d.destructive && !allowDestructive)">Approve and apply</button>
            <button type="button" class="secondary" (click)="decide(false)" [disabled]="busy()">Reject</button>
          </div>
          @if (message()) { <p role="status">{{ message() }}</p> }
        </section>
      } @else if (d.decidedAt) {
        <p class="meta">{{ d.approved ? 'Approved' : 'Rejected' }} {{ d.decidedAt | date: 'medium' }}{{ d.allowDestructive ? ' (destructive changes accepted)' : '' }}</p>
      }
      @if (d.result; as r) {
        <h2>Verification</h2>
        <table>
          <tbody>
            @if (r.health; as h) {
              <tr><td>Health ({{ h.url }})</td><td><span class="status" [attr.data-status]="h.healthy ? 'passed' : 'failed'">{{ h.healthy ? 'healthy' : 'unhealthy' }}</span>
                · HTTP {{ h.statusCode ?? '—' }} · {{ h.attempts }} attempt(s)</td></tr>
            }
            @for (ref of r.configuration?.references ?? []; track ref.name) {
              <tr><td>Secret reference {{ ref.name }}</td><td><span class="status" [attr.data-status]="ref.status === 'Resolved' ? 'passed' : 'failed'">{{ ref.status }}</span></td></tr>
            }
          </tbody>
        </table>
        @if (r.failure) { <details open><summary>Failure</summary><pre>{{ r.failure }}</pre></details> }
      }
      @if (active()) { <p role="status">{{ waiting() }}</p> }
    } @else if (failed()) { <p class="error" role="alert">The deployment could not be loaded.</p> }`,
  styles: `table { width: 100%; border-collapse: collapse; margin-bottom: 12px; } th, td { text-align: left; padding: 6px; border-bottom: 1px solid var(--border); }
    .meta { color: var(--muted); } .error { color: var(--danger); } pre { white-space: pre-wrap; overflow-wrap: anywhere; }
    dl { display: grid; grid-template-columns: max-content 1fr; gap: 4px 12px; } dd { margin: 0; overflow-wrap: anywhere; }
    .card { border: 1px solid var(--border); border-radius: 8px; padding: 16px; background: var(--surface); margin: 16px 0; }
    .actions { display: flex; gap: 10px; } .check { display: flex; gap: 8px; align-items: center; margin-bottom: 12px; }
    button { padding: 8px 16px; border-radius: 6px; border: 0; background: var(--accent); color: white; cursor: pointer; }
    button.secondary { background: var(--surface); color: var(--text); border: 1px solid var(--border); }
    .status[data-status="succeeded"], .status[data-status="passed"] { color: var(--ok); }
    .status[data-status="queued"], .status[data-status="planning"], .status[data-status="awaitingApproval"], .status[data-status="approved"],
    .status[data-status="applying"] { color: var(--warn); }
    .status[data-status="failed"], .status[data-status="unhealthy"], .status[data-status="rejected"], .status[data-status="expired"] { color: var(--danger); }`,
})
export class DeploymentPage implements OnInit {
  readonly id = input.required<string>();
  readonly deploymentId = input.required<string>();
  private readonly api = inject(FactoryApi);
  private readonly state = inject(WorkspaceState);
  private readonly destroy = inject(DestroyRef);
  protected readonly view = signal<DeploymentView | null>(null);
  protected readonly failed = signal(false);
  protected readonly busy = signal(false);
  protected readonly message = signal<string | null>(null);
  protected readonly active = computed(() => { const v = this.view(); return v ? activeDeployment(v.status) : false; });
  protected readonly waiting = computed(() => {
    switch (this.view()?.status) {
      case 'queued': case 'planning': return 'Building the release and planning…';
      case 'approved': case 'applying': return 'Applying the approved plan and verifying the release…';
      default: return '';
    }
  });
  protected readonly safeUrl = safeAppUrl;
  protected allowDestructive = false;
  private timer: ReturnType<typeof setTimeout> | undefined;

  ngOnInit(): void {
    void this.state.open(this.id());
    this.destroy.onDestroy(() => clearTimeout(this.timer));
    void this.load();
  }

  async load(): Promise<void> {
    clearTimeout(this.timer);
    try { this.view.set(await firstValueFrom(this.api.deployment(this.id(), this.deploymentId()))); }
    catch { this.failed.set(true); return; }
    if (this.active()) this.timer = setTimeout(() => void this.load(), pollMs);
  }

  async decide(approve: boolean): Promise<void> {
    const current = this.view();
    if (!current?.bindingSha256) return;
    this.busy.set(true); this.message.set(null);
    try {
      this.view.set(await firstValueFrom(this.api.decideDeployment(this.id(), this.deploymentId(), approve, current.bindingSha256,
        approve && !!current.destructive && this.allowDestructive)));
      if (this.active()) this.timer = setTimeout(() => void this.load(), pollMs);
    } catch (error) {
      const status = error instanceof HttpErrorResponse ? error.status : 0;
      this.message.set(status === 412 ? 'The plan changed or expired; request a new deployment.'
        : status === 403 ? 'Only owners and admins of the project’s organization can decide deployments.'
        : 'The decision could not be recorded.');
      if (status === 412) void this.load();
    } finally { this.busy.set(false); }
  }
}
