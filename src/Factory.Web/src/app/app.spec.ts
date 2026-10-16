import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { App } from './app';
import { AuthService, isLoopbackHttp } from './auth.service';
import { DecisionDialog } from './decision-dialog';
import { FactoryApi, RunPlan, RunSummary, RunView, WorkspaceProject } from './factory-api';
import { parseRequirements } from './project.page';
import { RunEventsService } from './run-events.service';
import { PendingDecision, WorkspaceState } from './workspace.state';
import { Subject } from 'rxjs';

const run = (id: string, createdAt: string, pendingDecisions = 0): RunSummary => ({
  runId: id, status: pendingDecisions ? 'waiting' : 'running', workflowVersion: 'decomposition-v1', failureCode: null,
  createdAt, updatedAt: createdAt, pendingDecisions,
});
const view = (id: string, reason = 'plan_approval'): RunView => ({
  runId: id, projectId: 'p1', status: 'waiting', failureCode: null, steps: [], publication: null, usage: null, routes: null,
  decisions: [{ id: 'd-' + id, ordinal: 4, reason, options: ['approve', 'stop'], status: 'pending', choice: null }],
});
const plan: RunPlan = {
  runId: 'r1', policyVersion: 'routing-v1', routes: [{ key: 'P1', tier: 'economy', reason: 'role_minimum' }],
  plan: { summary: 'One part', parts: [{ key: 'P1', title: 'Task entry', scope: 'Adding tasks', requirementIds: ['FR-1'], complexity: 'low', importance: 'normal', dependsOn: [] }] },
};

describe('parseRequirements', () => {
  it('reads one "ID: description" per line and ignores blank lines', () => {
    expect(parseRequirements(' USER-1: Add tasks \n\nUSER-2:Mark done')).toEqual([
      { id: 'USER-1', description: 'Add tasks' }, { id: 'USER-2', description: 'Mark done' }]);
  });
  it('returns null when nothing is entered and rejects malformed lines', () => {
    expect(parseRequirements('  \n ')).toBeNull();
    expect(() => parseRequirements('no separator')).toThrow();
    expect(() => parseRequirements(': missing id')).toThrow();
    expect(() => parseRequirements('USER-1:')).toThrow();
  });
});

describe('WorkspaceState', () => {
  it('surfaces the oldest pending decision and its plan', async () => {
    const api = {
      myProjects: () => of<WorkspaceProject[]>([{ projectId: 'p1', organizationId: 'o1', roles: ['Reviewer'], canDecide: true }]),
      runs: () => of([run('r2', '2026-10-16T10:00:00Z', 1), run('r1', '2026-10-16T09:00:00Z', 1), run('r0', '2026-10-16T08:00:00Z')]),
      run: (_: string, id: string) => of(view(id)),
      plan: () => of(plan),
    };
    TestBed.configureTestingModule({ providers: [{ provide: FactoryApi, useValue: api },
      { provide: RunEventsService, useValue: { changed: new Subject(), watch: () => Promise.resolve() } }] });
    const state = TestBed.inject(WorkspaceState);
    await state.open('p1');
    expect(state.pending()?.run.runId).toBe('r1');
    expect(state.pending()?.plan?.plan.parts[0].key).toBe('P1');
    expect(state.project()?.canDecide).toBe(true);
  });
});

describe('DecisionDialog', () => {
  function render(canDecide: boolean, pending: PendingDecision | null) {
    const decide = vi.fn().mockResolvedValue(undefined);
    TestBed.configureTestingModule({
      imports: [DecisionDialog],
      providers: [{ provide: WorkspaceState, useValue: {
        pending: signal(pending), project: signal({ projectId: 'p1', organizationId: 'o1', roles: [], canDecide }), decide } }],
    });
    const fixture = TestBed.createComponent(DecisionDialog);
    fixture.detectChanges();
    return { element: fixture.nativeElement as HTMLElement, decide };
  }

  it('blocks the page with the plan and offers only the decision options', async () => {
    const { element, decide } = render(true, { projectId: 'p1', run: view('r1'), decision: view('r1').decisions![0], plan });
    const dialog = element.querySelector('[role="alertdialog"]')!;
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(element.textContent).toContain('Task entry');
    expect(element.textContent).toContain('economy');
    const buttons = Array.from(element.querySelectorAll('button')).map(b => b.textContent?.trim());
    expect(buttons).toEqual(['approve', 'stop']); // no close or dismiss control
    (element.querySelector('button') as HTMLButtonElement).click();
    expect(decide).toHaveBeenCalledWith('approve');
  });

  it('tells members without authority that an administrator must decide', () => {
    const { element } = render(false, { projectId: 'p1', run: view('r1', 'budget_exceeded'), decision: view('r1', 'budget_exceeded').decisions![0], plan: null });
    expect(element.querySelectorAll('button').length).toBe(0);
    expect(element.textContent).toContain('Waiting for an organization administrator');
    expect(element.textContent).toContain('budget would be exceeded');
  });

  it('renders nothing without a pending decision', () => {
    const { element } = render(true, null);
    expect(element.querySelector('[role="alertdialog"]')).toBeNull();
  });
});

describe('App', () => {
  it('asks signed-out users to sign in and shows no workspace content', () => {
    const login = vi.fn();
    TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter([]), { provide: AuthService, useValue: { signedIn: signal(false), userName: signal(null), unavailable: signal(false), login, logout: vi.fn() } }],
    });
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.textContent).toContain('Sign in with your Factory account');
    expect(element.querySelector('app-decision-dialog')).toBeNull();
    (element.querySelector('.welcome button') as HTMLButtonElement).click();
    expect(login).toHaveBeenCalled();
  });
});

describe('isLoopbackHttp', () => {
  it('allows plain HTTP only for loopback issuers', () => {
    expect(isLoopbackHttp('http://127.0.0.1:58080/realms/factory-dev')).toBe(true);
    expect(isLoopbackHttp('http://localhost:8080/realms/x')).toBe(true);
    expect(isLoopbackHttp('http://keycloak.example.com/realms/x')).toBe(false);
    expect(isLoopbackHttp('http://127.0.0.1.example.com/realms/x')).toBe(false);
    expect(isLoopbackHttp('https://127.0.0.1/realms/x')).toBe(false);
  });
});
