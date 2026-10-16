import { DatePipe } from '@angular/common';
import { Component, OnInit, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ConfirmedRequirement, FactoryApi, decompositionVersion, standardVersion } from './factory-api';
import { DevelopmentsPanel } from './development.page';
import { VerificationsPanel } from './verification.page';
import { WorkspaceState } from './workspace.state';

// "ID: description" per line; blank lines ignored.
export function parseRequirements(text: string): ConfirmedRequirement[] | null {
  const lines = text.split('\n').map(l => l.trim()).filter(l => l.length > 0);
  if (lines.length === 0) return null;
  return lines.map(line => {
    const at = line.indexOf(':');
    if (at <= 0 || at === line.length - 1) throw new Error('invalid_requirement_line');
    return { id: line.slice(0, at).trim(), description: line.slice(at + 1).trim() };
  });
}

@Component({
  selector: 'app-project-page',
  imports: [FormsModule, RouterLink, DatePipe, VerificationsPanel, DevelopmentsPanel],
  template: `
    <p><a routerLink="/">Projects</a> / {{ id() }}</p>
    <h1>{{ id() }}</h1>
    <section class="card">
      <h2>New run</h2>
      <form (ngSubmit)="submit()">
        <label>Task <textarea name="task" [(ngModel)]="task" required rows="3"></textarea></label>
        <label>Context <textarea name="context" [(ngModel)]="context" rows="2"></textarea></label>
        <label>Confirmed requirements (one "ID: description" per line)
          <textarea name="requirements" [(ngModel)]="requirements" rows="3"></textarea></label>
        <label>Workflow
          <select name="workflow" [(ngModel)]="workflow">
            <option [value]="standard">Analysis and architecture</option>
            <option [value]="decomposed">Decomposed (plan approval, parallel parts)</option>
          </select>
        </label>
        <button type="submit" [disabled]="busy() || !task.trim()">Start run</button>
        @if (message()) { <span class="message" role="status">{{ message() }}</span> }
      </form>
    </section>
    <app-developments-panel [projectId]="id()" />
    <app-verifications-panel [projectId]="id()" />
    <h2>Runs</h2>
    @if (state.runs().length === 0) { <p>No runs yet.</p> }
    <table class="runs">
      <thead><tr><th>Started</th><th>Workflow</th><th>Status</th><th>Decisions</th></tr></thead>
      <tbody>
        @for (run of state.runs(); track run.runId) {
          <tr>
            <td><a [routerLink]="['/projects', id(), 'runs', run.runId]">{{ run.createdAt | date: 'medium' }}</a></td>
            <td>{{ run.workflowVersion === decomposed ? 'decomposed' : 'standard' }}</td>
            <td><span class="status" [attr.data-status]="run.status">{{ run.status }}</span>{{ run.failureCode ? ' (' + run.failureCode + ')' : '' }}</td>
            <td>{{ run.pendingDecisions || '' }}</td>
          </tr>
        }
      </tbody>
    </table>`,
  styles: `
    .card { border: 1px solid var(--border); border-radius: 8px; padding: 16px; margin-bottom: 24px; background: var(--surface); }
    form { display: grid; gap: 10px; } label { display: grid; gap: 4px; }
    textarea, select { font: inherit; padding: 6px; border: 1px solid var(--border); border-radius: 6px; background: var(--bg); color: var(--text); }
    button { justify-self: start; padding: 8px 16px; border-radius: 6px; border: 0; background: var(--accent); color: white; cursor: pointer; }
    .runs { width: 100%; border-collapse: collapse; } .runs th, .runs td { text-align: left; padding: 6px; border-bottom: 1px solid var(--border); }
    .status[data-status="succeeded"] { color: var(--ok); } .status[data-status="failed"], .status[data-status="cancelled"] { color: var(--danger); }
    .status[data-status="waiting"] { color: var(--warn); } .message { margin-left: 12px; }`,
})
export class ProjectPage implements OnInit {
  readonly id = input.required<string>();
  protected readonly state = inject(WorkspaceState);
  private readonly api = inject(FactoryApi);
  protected readonly standard = standardVersion;
  protected readonly decomposed = decompositionVersion;
  protected task = '';
  protected context = '';
  protected requirements = '';
  protected workflow: string = standardVersion;
  protected readonly busy = signal(false);
  protected readonly message = signal<string | null>(null);

  ngOnInit(): void { void this.state.open(this.id()); }

  async submit(): Promise<void> {
    let confirmed: ConfirmedRequirement[] | null;
    try { confirmed = parseRequirements(this.requirements); }
    catch { this.message.set('Each requirement line needs "ID: description".'); return; }
    this.busy.set(true); this.message.set(null);
    try {
      const submission = await firstValueFrom(this.api.submit(this.id(),
        { task: this.task.trim(), context: this.context.trim(), confirmedRequirements: confirmed, workflowVersion: this.workflow },
        crypto.randomUUID()));
      this.message.set(`Run ${submission.runId} queued.`);
      this.task = ''; this.context = ''; this.requirements = '';
      await this.state.refresh(this.id());
    } catch {
      this.message.set('The run could not be started.');
    } finally { this.busy.set(false); }
  }
}
