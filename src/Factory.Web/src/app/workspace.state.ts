import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { FactoryApi, RunPlan, RunSummary, RunView, StepDecision, WorkspaceProject } from './factory-api';
import { RunEventsService } from './run-events.service';

export interface PendingDecision { projectId: string; run: RunView; decision: StepDecision; plan: RunPlan | null; }

// Workspace state for the open project: its runs and the first pending decision, refreshed on
// every run change hint. The decision popup is driven from here and blocks the whole page.
@Injectable({ providedIn: 'root' })
export class WorkspaceState {
  private readonly api = inject(FactoryApi);
  private readonly events = inject(RunEventsService);
  readonly projects = signal<WorkspaceProject[]>([]);
  readonly projectId = signal<string | null>(null);
  readonly runs = signal<RunSummary[]>([]);
  readonly pending = signal<PendingDecision | null>(null);
  readonly error = signal<string | null>(null);
  readonly project = computed(() => this.projects().find(p => p.projectId === this.projectId()) ?? null);
  private generation = 0;

  constructor() {
    this.events.changed.subscribe(() => { const id = this.projectId(); if (id) void this.refresh(id); });
  }

  async loadProjects(): Promise<void> {
    try { this.projects.set(await firstValueFrom(this.api.myProjects())); this.error.set(null); }
    catch { this.error.set('projects_unavailable'); }
  }

  async open(projectId: string): Promise<void> {
    if (this.projects().length === 0) await this.loadProjects();
    this.projectId.set(projectId);
    await this.refresh(projectId);
    try { await this.events.watch(projectId); } catch { this.error.set('live_updates_unavailable'); }
  }

  // Responses for an older project or refresh are dropped.
  async refresh(projectId: string): Promise<void> {
    const generation = ++this.generation;
    try {
      const runs = await firstValueFrom(this.api.runs(projectId));
      if (generation !== this.generation) return;
      this.runs.set(runs);
      const waiting = runs.filter(r => r.pendingDecisions > 0).sort((a, b) => a.createdAt.localeCompare(b.createdAt))[0];
      if (!waiting) { this.pending.set(null); return; }
      const run = await firstValueFrom(this.api.run(projectId, waiting.runId));
      const decision = run.decisions?.find(d => d.status === 'pending');
      if (generation !== this.generation) return;
      if (!decision) { this.pending.set(null); return; }
      const plan = decision.reason === 'plan_approval' ? await firstValueFrom(this.api.plan(projectId, run.runId)) : null;
      if (generation === this.generation) this.pending.set({ projectId, run, decision, plan });
    } catch {
      if (generation === this.generation) this.error.set('runs_unavailable');
    }
  }

  async decide(choice: string): Promise<void> {
    const pending = this.pending();
    if (!pending) return;
    await firstValueFrom(this.api.decide(pending.projectId, pending.run.runId, pending.decision.id, choice));
    await this.refresh(pending.projectId);
  }
}
