import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { Router, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { DeployPanel, DeploymentPage, DeploymentsPanel, safeAppUrl } from './deployment.page';
import { DeploymentView, FactoryApi } from './factory-api';
import { WorkspaceState } from './workspace.state';

const binding = 'b'.repeat(64);
const planned = (destructive = false): DeploymentView => ({
  deploymentId: 'd1', targetName: 'stock-flow-staging', developmentJobId: 'j1', status: 'awaitingApproval', failureCode: null,
  bindingSha256: binding, releaseSha256: 'r'.repeat(64), moduleId: 'azure-app-service-postgres', moduleVersion: '1.1.0',
  summary: { add: 2, change: 5, destroy: destructive ? 1 : 0, changes: [{ address: 'azurerm_linux_web_app.api', type: 'azurerm_linux_web_app',
    actions: destructive ? ['delete', 'create'] : ['update'] }] },
  destructive, plannedAt: '2026-10-17T10:00:00Z', approved: null, decidedAt: null, allowDestructive: null, url: null, result: null,
  createdAt: '2026-10-17T09:59:00Z', updatedAt: '2026-10-17T10:00:00Z', planSha256: 'p'.repeat(64),
});

describe('deployment helpers', () => {
  it('renders only HTTPS App Service addresses', () => {
    expect(safeAppUrl('https://stock-flow-staging-9b228545.azurewebsites.net')).toBe(true);
    expect(safeAppUrl('http://stock-flow-staging-9b228545.azurewebsites.net')).toBe(false);
    expect(safeAppUrl('https://evil.example/x.azurewebsites.net')).toBe(false);
    expect(safeAppUrl('javascript:alert(1)')).toBe(false);
    expect(safeAppUrl(null)).toBe(false);
  });
});

describe('DeploymentPage', () => {
  async function render(view: DeploymentView, decide = vi.fn()) {
    TestBed.configureTestingModule({
      imports: [DeploymentPage],
      providers: [provideRouter([]), { provide: WorkspaceState, useValue: { open: vi.fn() } },
        { provide: FactoryApi, useValue: { deployment: vi.fn().mockReturnValue(of(view)), decideDeployment: decide } }],
    });
    const fixture = TestBed.createComponent(DeploymentPage);
    fixture.componentRef.setInput('id', 'p1'); fixture.componentRef.setInput('deploymentId', 'd1');
    fixture.detectChanges(); await fixture.whenStable(); fixture.detectChanges();
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  const button = (element: HTMLElement, text: string) =>
    Array.from(element.querySelectorAll('button')).find(b => b.textContent?.includes(text)) as HTMLButtonElement;

  it('shows the plan and approves exactly its binding', async () => {
    const decide = vi.fn().mockReturnValue(of({ ...planned(), status: 'succeeded', approved: true, decidedAt: '2026-10-17T10:05:00Z' }));
    const { element } = await render(planned(), decide);
    expect(element.textContent).toContain('azurerm_linux_web_app.api');
    expect(element.textContent).toContain(binding);
    button(element, 'Approve').click();
    await vi.waitFor(() => expect(decide).toHaveBeenCalledWith('p1', 'd1', true, binding, false));
  });

  it('requires accepting destructive changes before approval', async () => {
    const decide = vi.fn().mockReturnValue(of(planned(true)));
    const { fixture, element } = await render(planned(true), decide);
    expect(element.textContent).toContain('deletes or replaces resources');
    expect(button(element, 'Approve').disabled).toBe(true);
    (element.querySelector('input[type=checkbox]') as HTMLInputElement).click();
    fixture.detectChanges(); await fixture.whenStable(); fixture.detectChanges();
    expect(button(element, 'Approve').disabled).toBe(false);
    button(element, 'Approve').click();
    await vi.waitFor(() => expect(decide).toHaveBeenCalledWith('p1', 'd1', true, binding, true));
  });

  it('explains a stale plan and never renders an unsafe address', async () => {
    const decide = vi.fn().mockReturnValue(throwError(() => new HttpErrorResponse({ status: 412 })));
    const { fixture, element } = await render({ ...planned(), url: 'https://evil.example' }, decide);
    expect(element.querySelector('.url')).toBeNull();
    button(element, 'Reject').click();
    await vi.waitFor(() => { fixture.detectChanges(); expect(element.textContent).toContain('changed or expired'); });
  });

  it('shows the verified result with secret reference statuses', async () => {
    const url = 'https://stock-flow-staging-9b228545.azurewebsites.net';
    const { element } = await render({ ...planned(), status: 'succeeded', approved: true, decidedAt: '2026-10-17T10:05:00Z', url,
      result: { bindingSha256: binding, url, outputs: null,
        health: { url: url + '/health', healthy: true, statusCode: 200, attempts: 1, duration: '00:00:01' },
        configuration: { resolved: true, references: [{ name: 'ConnectionStrings__Database', status: 'Resolved' }] },
        failure: null, approvedBy: 'issuer|subject', approvedAt: '2026-10-17T10:05:00Z' } });
    const link = element.querySelector('.url a') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe(url);
    expect(link.getAttribute('rel')).toBe('noopener noreferrer');
    expect(element.textContent).toContain('ConnectionStrings__Database');
    expect(element.textContent).toContain('Resolved');
    expect(element.querySelector('.decision')).toBeNull();
  });
});

describe('DeployPanel', () => {
  it('plans a deployment to the selected target and opens it', async () => {
    const submit = vi.fn().mockReturnValue(of({ deploymentId: 'd9', status: 'queued', replayed: false }));
    TestBed.configureTestingModule({ imports: [DeployPanel], providers: [provideRouter([]), { provide: FactoryApi, useValue: {
      deployments: vi.fn().mockReturnValue(of({ targets: [{ name: 'stock-flow-staging', cloud: 'azure', environment: 'staging', application: 'stock-flow',
        location: 'francecentral', resourceGroup: 'rg-stock-flow-staging-frc' }], deployments: [] })), submitDeployment: submit } }] });
    const fixture = TestBed.createComponent(DeployPanel);
    fixture.componentRef.setInput('projectId', 'p1'); fixture.componentRef.setInput('jobId', 'j1');
    fixture.detectChanges(); await fixture.whenStable(); fixture.detectChanges(); await fixture.whenStable();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    ((fixture.nativeElement as HTMLElement).querySelector('button') as HTMLButtonElement).click();
    await vi.waitFor(() => expect(navigate).toHaveBeenCalledWith(['/projects', 'p1', 'deployments', 'd9']));
    expect(submit).toHaveBeenCalledWith('p1', 'stock-flow-staging', 'j1', expect.any(String));
  });
});

describe('DeploymentsPanel', () => {
  it('lists the project deployments with links', async () => {
    TestBed.configureTestingModule({ imports: [DeploymentsPanel], providers: [provideRouter([]), { provide: FactoryApi, useValue: {
      deployments: vi.fn().mockReturnValue(of({ targets: [], deployments: [{ ...planned(), status: 'succeeded' }] })) } }] });
    const fixture = TestBed.createComponent(DeploymentsPanel);
    fixture.componentRef.setInput('projectId', 'p1');
    fixture.detectChanges(); await fixture.whenStable(); fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('a')?.getAttribute('href')).toBe('/projects/p1/deployments/d1');
    expect(element.textContent).toContain('2 add · 5 change · 0 destroy');
  });
});
