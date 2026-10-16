import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { Router, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { DevelopmentPage, DevelopPanel, diffLines, usd } from './development.page';
import { DevelopmentView, FactoryApi } from './factory-api';
import { WorkspaceState } from './workspace.state';

describe('development helpers', () => {
  it('classifies unified diff lines', () => {
    expect(diffLines('diff --git a/x b/x\n--- a/x\n+++ b/x\n@@ -1 +1 @@\n-old\n+new\n same\n').map(l => l.kind))
      .toEqual(['file', 'file', 'file', 'hunk', 'remove', 'add', 'context']);
    expect(usd(9154)).toBe('0.0092');
    expect(usd(null)).toBe('');
  });
});

describe('DevelopPanel', () => {
  async function render(submit: ReturnType<typeof vi.fn>) {
    TestBed.configureTestingModule({ imports: [DevelopPanel], providers: [provideRouter([]), { provide: FactoryApi, useValue: { submitDevelopment: submit } }] });
    const fixture = TestBed.createComponent(DevelopPanel);
    for (const [k, v] of Object.entries({ projectId: 'p1', caseId: 'c1', version: 2, sha256: 'a'.repeat(64) })) fixture.componentRef.setInput(k, v);
    fixture.detectChanges();
    await fixture.whenStable(); // ngModel writes its initial value asynchronously
    const element = fixture.nativeElement as HTMLElement;
    const input = element.querySelector('input') as HTMLInputElement;
    input.value = 'StockFlow'; input.dispatchEvent(new Event('input')); fixture.detectChanges();
    return { fixture, element };
  }

  it('submits the exact approved version and opens the job', async () => {
    const submit = vi.fn().mockReturnValue(of({ jobId: 'j1', status: 'queued', replayed: false }));
    const { element } = await render(submit);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    (element.querySelector('button') as HTMLButtonElement).click();
    await vi.waitFor(() => expect(navigate).toHaveBeenCalledWith(['/projects', 'p1', 'developments', 'j1']));
    expect(submit).toHaveBeenCalledWith('p1', 'c1', 2, 'a'.repeat(64), 'StockFlow', expect.any(String));
  });

  it('explains a superseded approval', async () => {
    const { fixture, element } = await render(vi.fn().mockReturnValue(throwError(() => new HttpErrorResponse({ status: 412 }))));
    (element.querySelector('button') as HTMLButtonElement).click();
    await vi.waitFor(() => { fixture.detectChanges(); expect(element.textContent).toContain('no longer the approved one'); });
  });
});

describe('DevelopmentPage', () => {
  it('shows changes with requirements, the diff, verification and evidence', async () => {
    const view: DevelopmentView = {
      evidenceSha256: 'e'.repeat(64),
      job: { jobId: 'j1', caseId: 'c1', artifactVersion: 1, applicationName: 'StockFlow', status: 'verified', failureCode: null, costMicros: 9154,
        createdAt: '2026-10-16T10:00:00Z', updatedAt: '2026-10-16T10:01:00Z' },
      evidence: {
        formatVersion: 1, jobId: 'j1', caseId: 'c1', artifactVersion: 1, artifactSha256: 'a'.repeat(64), templateSha256: 't'.repeat(64),
        applicationName: 'StockFlow', model: 'gpt-5.4-mini', status: 'verified', failureCode: null, turns: 3, actions: 12, costMicros: 9154,
        calls: [{ turn: 1, status: 'completed', model: 'gpt-5.4-mini-2026-03-17', inputTokens: 952, outputTokens: 247, reservedMicros: 55881, costMicros: 1826 }],
        changes: [{ path: 'src/StockFlow.Domain/Product.cs', kind: 'added', requirementIds: ['FR-1', 'FR-2'] }],
        baseCommit: 'b'.repeat(40), finalCommit: 'c'.repeat(40), diff: 'diff --git a/x b/x\n+<script>alert(1)</script>\n', diffSha256: 'd'.repeat(64),
        verification: { runnerImage: 'sha256:' + 'r'.repeat(64), workspaceSha256: 'w'.repeat(64), outcome: 'passed', failureCode: null,
          startedAt: '2026-10-16T10:00:00Z', duration: '00:00:20', steps: [{ name: 'dotnet-test', outcome: 'passed', exitCode: 0, duration: '00:00:03', output: '', outputTruncated: false }],
          limits: { cpus: 2, memoryMb: 4096, pids: 1024, workspaceMb: 1024, tempMb: 512, stepTimeout: '00:10:00', jobTimeout: '00:20:00', outputBytes: 65536 } },
        log: ['turn 1: plan'],
      },
    };
    TestBed.configureTestingModule({ imports: [DevelopmentPage], providers: [provideRouter([]),
      { provide: FactoryApi, useValue: { development: () => of(view) } }, { provide: WorkspaceState, useValue: { open: () => Promise.resolve() } }] });
    const fixture = TestBed.createComponent(DevelopmentPage);
    fixture.componentRef.setInput('id', 'p1'); fixture.componentRef.setInput('jobId', 'j1');
    fixture.detectChanges(); await fixture.whenStable(); fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('.changes')?.textContent).toContain('FR-1, FR-2');
    expect(element.querySelector('pre.diff .add')?.textContent).toBe('+<script>alert(1)</script>');
    expect(element.querySelector('pre.diff script')).toBeNull(); // diff text is never rendered as HTML
    expect(element.textContent).toContain('0.0092');
    expect(element.textContent).toContain('dotnet-test');
    expect(element.textContent).toContain('e'.repeat(64));
  });
});
