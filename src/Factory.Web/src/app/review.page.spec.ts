import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { FactoryApi, ReviewArtifact, ReviewSnapshot } from './factory-api';
import { ReviewPage, decisionBody, reasonIsValid, sha256Hex, traceText } from './review.page';
import { WorkspaceState } from './workspace.state';

const artifact: ReviewArtifact = {
  formatVersion: 1, validatorVersion: 'milestone3-v1', caseId: 'c1', version: 2, workflowId: 'w1', createdAt: '2026-10-16T10:00:00Z',
  request: { projectId: 'p1', task: 'Design a task list', context: 'Single user', confirmedRequirements: [{ id: 'USER-1', description: 'Add tasks' }] },
  analysis: {
    summary: 'Task list',
    functionalRequirements: [
      { id: 'FR-1', description: 'Add tasks', status: 'confirmed', confirmedSourceId: 'USER-1' },
      { id: 'FR-2', description: 'Save tasks', status: 'suggested', confirmedSourceId: null },
      { id: 'FR-3', description: 'Sync devices', status: 'unverifiedAssumption', confirmedSourceId: null }],
    assumptions: [{ description: 'Local storage suffices', validationNeeded: 'Ask about sharing' }], risks: [], openQuestions: [{ question: 'Offline?' }],
  },
  architecture: {
    summary: 'One module', status: 'proposed', confirmedConstraintRequirementIds: ['FR-1'],
    components: [{ id: 'C-1', name: 'Tasks', responsibility: 'Handles tasks',
      basis: { isDesignRecommendation: true, requirements: [{ requirementId: 'FR-1', status: 'confirmed' }], assumptionDescriptions: [] } }],
    technologyDecisions: [{ id: 'T-1', technology: 'Plain module', rationale: 'Small', tradeOffs: ['Simplicity'],
      alternatives: [{ technology: 'Service', benefits: 'Isolation', drawbacks: 'Overhead' }],
      basis: { isDesignRecommendation: true, requirements: [], assumptionDescriptions: [] } }],
    risks: [], adrs: [{ id: 'ADR-1', technologyDecisionId: 'T-1', context: 'First iteration', consequences: 'Revisit' }],
  },
};

describe('reasonIsValid (DecisionReasonPolicy v1 mirror)', () => {
  it('lets approvals omit a reason but requires an explanation otherwise', () => {
    expect(reasonIsValid('approve', null)).toBe(true);
    expect(reasonIsValid('approve', ' 　')).toBe(true);
    expect(reasonIsValid('reject', null)).toBe(false);
    expect(reasonIsValid('reject', '   \t')).toBe(false);
    expect(reasonIsValid('requestChanges', 'Needs offline support')).toBe(true);
  });
  it('rejects NUL, lone surrogates and reasons over 4000 code units', () => {
    expect(reasonIsValid('reject', 'a\u0000')).toBe(false);
    expect(reasonIsValid('reject', 'a\uD800')).toBe(false);
    expect(reasonIsValid('reject', '\uDC00a')).toBe(false);
    expect(reasonIsValid('reject', '😀')).toBe(true);
    expect(reasonIsValid('reject', 'x'.repeat(4000))).toBe(true);
    expect(reasonIsValid('reject', 'x'.repeat(4001))).toBe(false);
  });
});

describe('decisionBody', () => {
  it('binds the version and hash and sends no reason for an unexplained approval', () => {
    expect(decisionBody('approve', 2, 'ab', '  ')).toEqual({ decision: 'approve', artifactVersion: 2, artifactSha256: 'ab', reason: null });
    expect(decisionBody('approve', 2, 'ab', 'Looks right')).toEqual({ decision: 'approve', artifactVersion: 2, artifactSha256: 'ab', reason: 'Looks right' });
    expect(decisionBody('reject', 3, 'cd', 'Wrong scope').reason).toBe('Wrong scope');
  });
});

describe('sha256Hex and traceText', () => {
  it('hashes bytes as lowercase hex', async () => {
    expect(await sha256Hex(new TextEncoder().encode('abc').buffer as ArrayBuffer))
      .toBe('ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad');
  });
  it('keeps requirement provenance visible', () => {
    expect(traceText({ isDesignRecommendation: true, requirements: [{ requirementId: 'FR-3', status: 'unverifiedAssumption' }], assumptionDescriptions: ['A'] }))
      .toBe('FR-3 (unverified assumption), assumptions: A');
    expect(traceText({ isDesignRecommendation: true, requirements: [], assumptionDescriptions: [] })).toBe('design recommendation, no requirement trace');
  });
});

describe('ReviewPage', () => {
  async function render(tamper: boolean) {
    const bytes = new TextEncoder().encode(JSON.stringify(artifact));
    const hash = await sha256Hex(bytes.buffer as ArrayBuffer);
    const snapshot: ReviewSnapshot = { id: 'c1', projectId: 'p1', currentVersion: 2, contentHash: tamper ? 'f'.repeat(64) : hash,
      revision: 3, state: 'pendingReview', decision: null };
    const decided: ReviewSnapshot = { ...snapshot, revision: 4, state: 'approved',
      decision: { id: 'd1', decision: 'approve', reason: null, principalId: 'u1', createdAt: '2026-10-16T11:00:00Z' } };
    const decideReview = vi.fn().mockReturnValue(of({ snapshot: decided, etag: '"review-c1-r4"' }));
    TestBed.configureTestingModule({
      imports: [ReviewPage],
      providers: [provideRouter([]),
        { provide: FactoryApi, useValue: { review: () => of({ snapshot, etag: '"review-c1-r3"' }), reviewArtifact: () => of(bytes.buffer), decideReview } },
        { provide: WorkspaceState, useValue: { open: () => Promise.resolve(), project: signal({ projectId: 'p1', organizationId: 'o1', roles: ['Generator', 'Reviewer'], canDecide: true }) } }],
    });
    const fixture = TestBed.createComponent(ReviewPage);
    fixture.componentRef.setInput('id', 'p1');
    fixture.componentRef.setInput('caseId', 'c1');
    const element = fixture.nativeElement as HTMLElement;
    // Hashing (Web Crypto) is not tracked by Angular's stability; wait for the rendered verdict.
    await vi.waitFor(() => { fixture.detectChanges(); expect(element.textContent).toMatch(/content (verified|does not match)/); });
    return { fixture, element, decideReview, hash };
  }
  const button = (element: HTMLElement, text: string) =>
    Array.from(element.querySelectorAll('button')).find(b => b.textContent?.trim() === text) as HTMLButtonElement;

  it('shows the verified proposal with requirement provenance and approves the exact bytes', async () => {
    const { fixture, element, decideReview, hash } = await render(false);
    expect(element.textContent).toContain('content verified');
    expect(element.textContent).toContain('unverified assumption');
    expect(element.textContent).toContain('from USER-1');
    expect(element.textContent).toContain('Plain module');
    expect(button(element, 'Reject').disabled).toBe(true); // no reason yet
    button(element, 'Approve').click();
    await vi.waitFor(() => { fixture.detectChanges(); expect(element.textContent).toContain('Decision recorded.'); });
    expect(decideReview).toHaveBeenCalledWith('p1', 'c1', '"review-c1-r3"', expect.any(String),
      { decision: 'approve', artifactVersion: 2, artifactSha256: hash, reason: null });
    expect(element.textContent).toContain('Decision recorded.');
    expect(element.querySelector('.state')?.textContent).toBe('approved');
    // An approved, verified case offers development to generators.
    expect(element.querySelector('app-develop-panel')).not.toBeNull();
  });

  it('disables every decision when the content does not match the published hash', async () => {
    const { element } = await render(true);
    expect(element.textContent).toContain('content does not match the published hash');
    expect(button(element, 'Approve').disabled).toBe(true);
  });
});
