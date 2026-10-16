import { DecimalPipe } from '@angular/common';
import { Component, DestroyRef, OnInit, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { FactoryApi, RunPlan, RunView } from './factory-api';
import { RunEventsService } from './run-events.service';
import { WorkspaceState } from './workspace.state';

@Component({
  selector: 'app-run-page',
  imports: [RouterLink, DecimalPipe],
  template: `
    <p><a routerLink="/">Projects</a> / <a [routerLink]="['/projects', id()]">{{ id() }}</a> / run</p>
    @if (view(); as run) {
      <h1>Run <span class="status" [attr.data-status]="run.status">{{ run.status }}</span></h1>
      <p class="meta">{{ run.runId }}{{ run.failureCode ? ' · ' + run.failureCode : '' }}</p>
      @if (run.status === 'queued' || run.status === 'running' || run.status === 'waiting') {
        <button type="button" class="danger" (click)="cancel()">Cancel run</button>
      }
      <h2>Steps</h2>
      <table>
        <thead><tr><th>#</th><th>Step</th><th>Status</th><th>Attempts</th><th>Model tier</th><th>Model</th></tr></thead>
        <tbody>
          @for (step of run.steps; track step.ordinal) {
            <tr>
              <td>{{ step.ordinal }}</td><td>{{ step.kind }}</td><td>{{ step.status }}</td><td>{{ step.attempts }}</td>
              <td>{{ route(step.ordinal)?.tier ?? '' }}</td><td>{{ route(step.ordinal)?.model ?? '' }}</td>
            </tr>
          }
        </tbody>
      </table>
      @if (run.usage; as usage) {
        <h2>Usage</h2>
        <p>{{ usage.calls }} model calls · {{ usage.inputTokens }} input / {{ usage.outputTokens }} output tokens ·
          estimated USD {{ usage.estimatedCostMicros / 1000000 | number: '1.6-6' }}
          @if (usage.unpricedCalls || usage.unknownUsageCalls) { · {{ usage.unpricedCalls }} unpriced, {{ usage.unknownUsageCalls }} unknown }</p>
      }
      @if (plan(); as p) {
        <h2>Plan</h2>
        <p>{{ p.plan.summary }}</p>
        <ul>@for (part of p.plan.parts; track part.key) { <li><strong>{{ part.key }}</strong> {{ part.title }} — {{ part.scope }}</li> }</ul>
      }
      @if (run.publication; as published) {
        <h2>Published</h2>
        <p>Review case {{ published.caseId }}, version {{ published.version }}</p>
      }
    } @else if (failed()) { <p class="error" role="alert">The run could not be loaded.</p> }`,
  styles: `table { width: 100%; border-collapse: collapse; } th, td { text-align: left; padding: 6px; border-bottom: 1px solid var(--border); }
    .meta { color: var(--muted); } .error { color: var(--danger); }
    button.danger { padding: 6px 12px; border-radius: 6px; border: 0; background: var(--danger); color: white; cursor: pointer; }
    .status[data-status="succeeded"] { color: var(--ok); } .status[data-status="failed"], .status[data-status="cancelled"] { color: var(--danger); }
    .status[data-status="waiting"] { color: var(--warn); }`,
})
export class RunPage implements OnInit {
  readonly id = input.required<string>();
  readonly runId = input.required<string>();
  private readonly api = inject(FactoryApi);
  private readonly state = inject(WorkspaceState);
  private readonly events = inject(RunEventsService);
  private readonly destroy = inject(DestroyRef);
  protected readonly view = signal<RunView | null>(null);
  protected readonly plan = signal<RunPlan | null>(null);
  protected readonly failed = signal(false);

  ngOnInit(): void {
    void this.state.open(this.id());
    void this.load();
    // Change hints for this run trigger a re-read of the authoritative view.
    this.events.changed.pipe(takeUntilDestroyed(this.destroy)).subscribe(change => { if (change.runId === this.runId()) void this.load(); });
  }

  protected route(ordinal: number) {
    return this.view()?.routes?.filter(r => r.ordinal === ordinal).at(-1) ?? null;
  }

  async load(): Promise<void> {
    try {
      const view = await firstValueFrom(this.api.run(this.id(), this.runId()));
      this.view.set(view);
      const planned = view.steps.some(s => s.kind === 'planning' && s.status === 'succeeded');
      if (planned && !this.plan()) this.plan.set(await firstValueFrom(this.api.plan(this.id(), this.runId())));
    } catch { this.failed.set(true); }
  }

  async cancel(): Promise<void> {
    try { this.view.set(await firstValueFrom(this.api.cancel(this.id(), this.runId()))); }
    catch { this.failed.set(true); }
  }

}
