import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import {
  DesignBasis, FactoryApi, RequirementStatus, ReviewArtifact, ReviewDecision, ReviewDecisionBody, ReviewSnapshot,
} from './factory-api';
import { WorkspaceState } from './workspace.state';

// Mirror of the server's DecisionReasonPolicy version 1 (explicit code points). The server stays
// authoritative; this only keeps reject/request-changes disabled until a reason is entered.
const reasonWhitespace = '\u0009\u000A\u000B\u000C\u000D \u0085         ' +
  '        　';
export function reasonIsValid(decision: ReviewDecision, reason: string | null): boolean {
  if (reason === null) return decision === 'approve';
  if (reason.length > 4000) return false;
  let explained = false;
  for (let i = 0; i < reason.length; i++) {
    const c = reason.charCodeAt(i);
    if (c === 0 || (c >= 0xdc00 && c <= 0xdfff)) return false;
    if (c >= 0xd800 && c <= 0xdbff) {
      const next = reason.charCodeAt(++i);
      if (!(next >= 0xdc00 && next <= 0xdfff)) return false;
    }
    if (!reasonWhitespace.includes(reason[i])) explained = true;
  }
  return decision === 'approve' || explained;
}

// The decision names the exact artifact version and the SHA-256 of the bytes the reviewer saw.
// An approval without an explanation is sent without a reason.
export function decisionBody(decision: ReviewDecision, version: number, sha256: string, reason: string): ReviewDecisionBody {
  const explained = reason.trim().length > 0;
  return { decision, artifactVersion: version, artifactSha256: sha256, reason: explained || decision !== 'approve' ? reason : null };
}

export async function sha256Hex(bytes: ArrayBuffer): Promise<string> {
  const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', bytes));
  return Array.from(digest, b => b.toString(16).padStart(2, '0')).join('');
}

export function traceText(basis: DesignBasis): string {
  const traces = basis.requirements.map(r => `${r.requirementId} (${statusLabel(r.status)})`);
  if (basis.assumptionDescriptions.length) traces.push(`assumptions: ${basis.assumptionDescriptions.join('; ')}`);
  return traces.length ? traces.join(', ') : 'design recommendation, no requirement trace';
}

export function statusLabel(status: RequirementStatus): string {
  return status === 'unverifiedAssumption' ? 'unverified assumption' : status;
}

const stateLabels: Record<ReviewSnapshot['state'], string> = {
  pendingReview: 'pending review', approved: 'approved', rejected: 'rejected', changesRequested: 'changes requested',
};

@Component({
  selector: 'app-review-page',
  imports: [RouterLink, DatePipe, FormsModule],
  template: `
    <p><a routerLink="/">Projects</a> / <a [routerLink]="['/projects', id()]">{{ id() }}</a> / review</p>
    @if (snapshot(); as review) {
      <h1>Review <span class="state" [attr.data-state]="review.state">{{ stateLabel() }}</span></h1>
      <p class="meta">Case {{ review.id }} · version {{ review.currentVersion }} · SHA-256 {{ review.contentHash.slice(0, 12) }}…
        @if (verified() === true) { · <span class="ok">content verified</span> }
        @if (verified() === false) { · <span class="error">content does not match the published hash</span> }</p>

      @if (artifact(); as a) {
        <section>
          <h2>Request</h2>
          <p class="text">{{ a.request.task }}</p>
          @if (a.request.context) { <p class="text muted">{{ a.request.context }}</p> }
          @if (a.request.confirmedRequirements?.length) {
            <ul>@for (r of a.request.confirmedRequirements; track r.id) { <li><strong>{{ r.id }}</strong> {{ r.description }}</li> }</ul>
          }
        </section>

        <section>
          <h2>Requirements analysis</h2>
          <p class="text">{{ a.analysis.summary }}</p>
          <table class="requirements">
            <thead><tr><th>ID</th><th>Requirement</th><th>Status</th></tr></thead>
            <tbody>
              @for (r of a.analysis.functionalRequirements; track r.id) {
                <tr><td>{{ r.id }}</td><td>{{ r.description }}</td>
                  <td><span class="badge" [attr.data-status]="r.status">{{ label(r.status) }}</span>
                    {{ r.confirmedSourceId ? 'from ' + r.confirmedSourceId : '' }}</td></tr>
              }
            </tbody>
          </table>
          <p class="note">Only requirements marked confirmed come from your input; suggested requirements and assumptions are proposals.</p>
          @if (a.analysis.assumptions.length) {
            <h3>Assumptions</h3>
            <ul>@for (x of a.analysis.assumptions; track $index) { <li>{{ x.description }} <span class="muted">— validate: {{ x.validationNeeded }}</span></li> }</ul>
          }
          @if (a.analysis.risks.length) {
            <h3>Risks</h3>
            <ul>@for (x of a.analysis.risks; track $index) { <li>{{ x.description }} <span class="muted">— mitigation: {{ x.suggestedMitigation }}</span></li> }</ul>
          }
          @if (a.analysis.openQuestions.length) {
            <h3>Open questions</h3>
            <ul>@for (x of a.analysis.openQuestions; track $index) { <li>{{ x.question }}</li> }</ul>
          }
        </section>

        <section>
          <h2>Architecture proposal <span class="badge" data-status="suggested">{{ a.architecture.status }}</span></h2>
          <p class="text">{{ a.architecture.summary }}</p>
          @if (a.architecture.confirmedConstraintRequirementIds.length) {
            <p>Constrained by confirmed requirements: {{ a.architecture.confirmedConstraintRequirementIds.join(', ') }}</p>
          }
          <h3>Components</h3>
          <table>
            <thead><tr><th>ID</th><th>Component</th><th>Responsibility</th><th>Traces</th></tr></thead>
            <tbody>
              @for (c of a.architecture.components; track c.id) {
                <tr><td>{{ c.id }}</td><td>{{ c.name }}</td><td>{{ c.responsibility }}</td><td>{{ trace(c.basis) }}</td></tr>
              }
            </tbody>
          </table>
          <h3>Technology decisions</h3>
          @for (t of a.architecture.technologyDecisions; track t.id) {
            <div class="decision">
              <p><strong>{{ t.id }} {{ t.technology }}</strong> — {{ t.rationale }}</p>
              @if (t.alternatives.length) {
                <ul>@for (alt of t.alternatives; track $index) { <li>Alternative {{ alt.technology }}: + {{ alt.benefits }}; − {{ alt.drawbacks }}</li> }</ul>
              }
              @if (t.tradeOffs.length) { <p class="muted">Trade-offs: {{ t.tradeOffs.join('; ') }}</p> }
              <p class="muted">Traces: {{ trace(t.basis) }}</p>
            </div>
          }
          @if (a.architecture.risks.length) {
            <h3>Architecture risks</h3>
            <ul>@for (r of a.architecture.risks; track r.id) { <li><strong>{{ r.id }}</strong> {{ r.description }} <span class="muted">— mitigation: {{ r.mitigation }}</span></li> }</ul>
          }
          @if (a.architecture.adrs.length) {
            <h3>Architecture decision records</h3>
            <ul>@for (adr of a.architecture.adrs; track adr.id) { <li><strong>{{ adr.id }}</strong> ({{ adr.technologyDecisionId }}) {{ adr.context }} <span class="muted">— consequences: {{ adr.consequences }}</span></li> }</ul>
          }
        </section>
      }

      <section class="card">
        <h2>Decision</h2>
        @if (review.decision; as d) {
          <p><strong>{{ d.decision }}</strong> on {{ d.createdAt | date: 'medium' }}</p>
          @if (d.reason) { <p class="text">{{ d.reason }}</p> }
        } @else if (review.state === 'pendingReview') {
          <label>Reason (required to reject or request changes)
            <textarea name="reason" [(ngModel)]="reason" rows="3" maxlength="4000"></textarea></label>
          <div class="actions">
            <button type="button" class="approve" [disabled]="!canDecide()" (click)="decide('approve')">Approve</button>
            <button type="button" [disabled]="!canDecide() || !explained()" (click)="decide('requestChanges')">Request changes</button>
            <button type="button" class="danger" [disabled]="!canDecide() || !explained()" (click)="decide('reject')">Reject</button>
          </div>
          @if (verified() === false) { <p class="error">Decisions are disabled because the content could not be verified.</p> }
        }
        @if (message()) { <p role="status">{{ message() }}</p> }
      </section>
    } @else if (failure()) { <p class="error" role="alert">{{ failure() }}</p> }`,
  styles: `table { width: 100%; border-collapse: collapse; margin-bottom: 12px; }
    th, td { text-align: left; padding: 6px; border-bottom: 1px solid var(--border); vertical-align: top; }
    .meta, .muted, .note { color: var(--muted); } .note { font-size: 0.9em; } .error { color: var(--danger); } .ok { color: var(--ok); }
    .text { white-space: pre-wrap; } section { margin-bottom: 24px; }
    .badge { padding: 1px 6px; border-radius: 4px; border: 1px solid var(--border); font-size: 0.85em; }
    .badge[data-status="confirmed"] { color: var(--ok); } .badge[data-status="suggested"] { color: var(--warn); }
    .badge[data-status="unverifiedAssumption"] { color: var(--danger); }
    .state[data-state="approved"] { color: var(--ok); } .state[data-state="rejected"] { color: var(--danger); }
    .state[data-state="pendingReview"], .state[data-state="changesRequested"] { color: var(--warn); }
    .decision { border-left: 3px solid var(--border); padding-left: 10px; margin-bottom: 10px; }
    .card { border: 1px solid var(--border); border-radius: 8px; padding: 16px; background: var(--surface); }
    label { display: grid; gap: 4px; margin-bottom: 10px; }
    textarea { font: inherit; padding: 6px; border: 1px solid var(--border); border-radius: 6px; background: var(--bg); color: var(--text); }
    .actions { display: flex; gap: 8px; flex-wrap: wrap; }
    button { padding: 8px 16px; border-radius: 6px; border: 0; background: var(--accent); color: white; cursor: pointer; }
    button.approve { background: var(--ok); } button.danger { background: var(--danger); }`,
})
export class ReviewPage implements OnInit {
  readonly id = input.required<string>();
  readonly caseId = input.required<string>();
  private readonly api = inject(FactoryApi);
  private readonly state = inject(WorkspaceState);
  protected readonly snapshot = signal<ReviewSnapshot | null>(null);
  protected readonly artifact = signal<ReviewArtifact | null>(null);
  protected readonly verified = signal<boolean | null>(null);
  protected readonly failure = signal<string | null>(null);
  protected readonly message = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly reasonText = signal('');
  protected readonly stateLabel = computed(() => { const s = this.snapshot(); return s ? stateLabels[s.state] : ''; });
  protected readonly explained = computed(() => reasonIsValid('reject', this.reasonText()));
  protected readonly canDecide = computed(() => this.verified() === true && !this.busy());
  protected readonly label = statusLabel;
  protected readonly trace = traceText;
  private etag = '';
  private hash = '';
  // One key per decision attempt; kept after a network failure so a retry replays instead of deciding twice.
  private key = crypto.randomUUID();

  protected get reason(): string { return this.reasonText(); }
  protected set reason(value: string) { this.reasonText.set(value); }

  ngOnInit(): void {
    void this.state.open(this.id());
    void this.load();
  }

  async load(): Promise<void> {
    try {
      const review = await firstValueFrom(this.api.review(this.id(), this.caseId()));
      this.snapshot.set(review.snapshot);
      this.etag = review.etag;
      const bytes = await firstValueFrom(this.api.reviewArtifact(this.id(), this.caseId(), review.snapshot.currentVersion));
      this.hash = await sha256Hex(bytes);
      const parsed = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes)) as ReviewArtifact;
      // The decision binds to these exact bytes; anything else disables deciding.
      this.verified.set(this.hash === review.snapshot.contentHash && parsed.caseId === review.snapshot.id &&
        parsed.version === review.snapshot.currentVersion && parsed.formatVersion === 1);
      this.artifact.set(parsed);
    } catch (error) {
      this.failure.set(error instanceof HttpErrorResponse && error.status === 403
        ? 'Only reviewers of this project can open its review cases.'
        : 'The review could not be loaded.');
    }
  }

  async decide(decision: ReviewDecision): Promise<void> {
    const review = this.snapshot();
    if (!review || !this.canDecide()) return;
    this.busy.set(true); this.message.set(null);
    try {
      const result = await firstValueFrom(this.api.decideReview(this.id(), this.caseId(), this.etag, this.key,
        decisionBody(decision, review.currentVersion, this.hash, this.reasonText())));
      this.snapshot.set(result.snapshot);
      if (result.etag) this.etag = result.etag;
      this.key = crypto.randomUUID();
      this.message.set('Decision recorded.');
    } catch (error) {
      const status = error instanceof HttpErrorResponse ? error.status : 0;
      if (status === 0) { this.message.set('The decision was not confirmed; try again.'); return; }
      this.key = crypto.randomUUID();
      if (status === 412 || status === 409) {
        this.message.set('The review changed in the meantime; it was reloaded. Check it before deciding.');
        this.artifact.set(null); this.verified.set(null);
        await this.load();
      } else if (status === 403) this.message.set('Only reviewers of this project can decide.');
      else this.message.set('The decision was rejected.');
    } finally { this.busy.set(false); }
  }
}
