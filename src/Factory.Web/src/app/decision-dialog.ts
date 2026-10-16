import { Component, computed, inject, signal } from '@angular/core';
import { WorkspaceState } from './workspace.state';

const reasons: Record<string, string> = {
  plan_approval: 'Approve the plan before any part is designed.',
  budget_exceeded: 'A budget would be exceeded by the next model call.',
  model_price_unknown: 'The selected model has no price while a budget applies.',
  customer_model_unavailable: "Your organization's model could not be used. Fix it and continue, or stop the run.",
};

// Blocking decision popup: modal, no close button, no Escape, no backdrop dismissal and no timeout.
// The run waits until an organization Owner/Admin chooses one of the offered options.
@Component({
  selector: 'app-decision-dialog',
  template: `
    @if (state.pending(); as pending) {
      <div class="backdrop" role="presentation">
        <section class="dialog" role="alertdialog" aria-modal="true" aria-labelledby="decision-title" aria-describedby="decision-text">
          <h2 id="decision-title">Decision required</h2>
          <p id="decision-text">{{ reason() }}</p>
          <p class="meta">Run {{ pending.run.runId }}</p>
          @if (pending.plan; as plan) {
            <p class="summary">{{ plan.plan.summary }}</p>
            <table>
              <thead><tr><th>Part</th><th>Scope</th><th>Requirements</th><th>Complexity</th><th>Importance</th><th>Depends on</th><th>Model tier</th></tr></thead>
              <tbody>
                @for (part of plan.plan.parts; track part.key; let i = $index) {
                  <tr>
                    <td><strong>{{ part.key }}</strong> {{ part.title }}</td><td>{{ part.scope }}</td>
                    <td>{{ part.requirementIds.join(', ') }}</td><td>{{ part.complexity }}</td><td>{{ part.importance }}</td>
                    <td>{{ part.dependsOn.join(', ') || '—' }}</td><td>{{ plan.routes[i]?.tier }}</td>
                  </tr>
                }
              </tbody>
            </table>
          }
          @if (state.project()?.canDecide) {
            <div class="actions">
              @for (option of pending.decision.options; track option) {
                <button type="button" [class.danger]="option === 'stop'" [disabled]="busy()" (click)="choose(option)">{{ option }}</button>
              }
            </div>
          } @else {
            <p class="wait">Waiting for an organization administrator to decide.</p>
          }
          @if (failed()) { <p class="error" role="alert">The decision could not be recorded. Try again.</p> }
        </section>
      </div>
    }`,
  styles: `
    .backdrop { position: fixed; inset: 0; background: rgb(15 23 42 / 0.6); display: grid; place-items: center; z-index: 10; }
    .dialog { background: var(--surface); color: var(--text); max-width: min(960px, 94vw); max-height: 90vh; overflow: auto; padding: 24px; border-radius: 8px; }
    .meta { color: var(--muted); font-size: 0.85rem; }
    table { border-collapse: collapse; width: 100%; margin: 12px 0; font-size: 0.9rem; }
    th, td { border-bottom: 1px solid var(--border); padding: 6px; text-align: left; vertical-align: top; }
    .actions { display: flex; gap: 8px; justify-content: flex-end; }
    button { padding: 8px 16px; border-radius: 6px; border: 1px solid var(--border); background: var(--accent); color: white; cursor: pointer; }
    button.danger { background: var(--danger); }
    .error { color: var(--danger); }
  `,
})
export class DecisionDialog {
  protected readonly state = inject(WorkspaceState);
  protected readonly busy = signal(false);
  protected readonly failed = signal(false);
  protected readonly reason = computed(() => {
    const code = this.state.pending()?.decision.reason ?? '';
    return reasons[code] ?? `Decision needed (${code}).`;
  });

  async choose(option: string): Promise<void> {
    this.busy.set(true); this.failed.set(false);
    try { await this.state.decide(option); }
    catch { this.failed.set(true); }
    finally { this.busy.set(false); }
  }
}
