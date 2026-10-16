import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { FactoryApi, VerificationSummary, VerificationView } from './factory-api';
import { VerificationPage, VerificationsPanel, activeVerification, isApplicationName, seconds } from './verification.page';
import { WorkspaceState } from './workspace.state';

const job = (status: VerificationSummary['status']): VerificationSummary => ({
  jobId: 'j1', templateId: 'dotnet-angular-postgres-ddd', templateVersion: '1.0.0', applicationName: 'SampleShop', status,
  failureCode: status === 'failed' ? 'step_failed' : null, attempts: 1, createdAt: '2026-10-16T10:00:00Z', updatedAt: '2026-10-16T10:01:00Z',
});

describe('verification helpers', () => {
  it('accepts only names the Factory accepts', () => {
    for (const name of ['StockFlow', 'Shop2Go', 'Inventory']) expect(isApplicationName(name)).toBe(true);
    for (const name of ['', 'stockFlow', 'API', 'Stock_Flow', 'Stock Flow', 'S', 'OneTwoThreeFourFiveSixSeven', 'Stöck']) expect(isApplicationName(name)).toBe(false);
  });
  it('formats durations and recognizes active jobs', () => {
    expect(seconds('00:01:02.5000000')).toBe('62.5 s');
    expect(seconds('1.00:00:01')).toBe('86401 s');
    expect([activeVerification('queued'), activeVerification('running'), activeVerification('passed'), activeVerification('timedOut')])
      .toEqual([true, true, false, false]);
  });
});

describe('VerificationsPanel', () => {
  it('submits a valid name for the starter template and lists the jobs', async () => {
    const submitVerification = vi.fn().mockReturnValue(of({ jobId: 'j1', status: 'queued', replayed: false }));
    const verifications = vi.fn().mockReturnValue(of([job('passed')]));
    TestBed.configureTestingModule({ imports: [VerificationsPanel], providers: [provideRouter([]),
      { provide: FactoryApi, useValue: { verifications, submitVerification } }] });
    const fixture = TestBed.createComponent(VerificationsPanel);
    fixture.componentRef.setInput('projectId', 'p1');
    fixture.detectChanges();
    await fixture.whenStable(); fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('.verifications')?.textContent).toContain('SampleShop');
    const button = element.querySelector('button') as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    const input = element.querySelector('input') as HTMLInputElement;
    input.value = 'not valid'; input.dispatchEvent(new Event('input')); fixture.detectChanges();
    expect(button.disabled).toBe(true);
    input.value = 'StockFlow'; input.dispatchEvent(new Event('input')); fixture.detectChanges();
    expect(button.disabled).toBe(false);
    button.click();
    await vi.waitFor(() => expect(submitVerification).toHaveBeenCalledWith('p1', 'StockFlow', expect.any(String)));
  });
});

describe('VerificationPage', () => {
  it('shows each sandbox step, its output and the evidence hashes', async () => {
    const view: VerificationView = {
      job: job('failed'), evidenceSha256: 'e'.repeat(64),
      evidence: {
        formatVersion: 1, jobId: 'j1', projectId: 'p1', templateId: 'dotnet-angular-postgres-ddd', templateVersion: '1.0.0',
        templateSha256: 't'.repeat(64), applicationName: 'SampleShop', manifestSha256: 'm'.repeat(64),
        report: {
          runnerImage: 'sha256:' + 'r'.repeat(64), workspaceSha256: 'm'.repeat(64), outcome: 'failed', failureCode: 'step_failed',
          startedAt: '2026-10-16T10:00:00Z', duration: '00:00:20.0000000',
          limits: { cpus: 2, memoryMb: 4096, pids: 1024, workspaceMb: 1024, tempMb: 512, stepTimeout: '00:10:00', jobTimeout: '00:20:00', outputBytes: 65536 },
          steps: [
            { name: 'dotnet-test', outcome: 'failed', exitCode: 1, duration: '00:00:03.0000000', output: 'Failed! 1 test', outputTruncated: true },
            { name: 'npm-ci', outcome: 'skipped', exitCode: null, duration: '00:00:00', output: '', outputTruncated: false },
          ],
        },
      },
    };
    TestBed.configureTestingModule({ imports: [VerificationPage], providers: [provideRouter([]),
      { provide: FactoryApi, useValue: { verification: () => of(view) } },
      { provide: WorkspaceState, useValue: { open: () => Promise.resolve() } }] });
    const fixture = TestBed.createComponent(VerificationPage);
    fixture.componentRef.setInput('id', 'p1'); fixture.componentRef.setInput('jobId', 'j1');
    fixture.detectChanges();
    await fixture.whenStable(); fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('step_failed');
    expect(text).toContain('dotnet-test');
    expect(text).toContain('skipped');
    expect(text).toContain('3 s');
    expect(text).toContain('dotnet-test output (truncated)');
    expect(text).toContain('e'.repeat(64));
    expect(text).toContain('no network');
    expect(text).not.toContain('Waiting for a worker');
  });
});
