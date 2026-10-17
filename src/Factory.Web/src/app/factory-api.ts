import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import { factoryConfig } from './factory.config';

// Wire contracts of the Factory API (camelCase JSON, camelCase enum values).
export type RunStatus = 'queued' | 'running' | 'waiting' | 'succeeded' | 'failed' | 'cancelled';
export type StepStatus = 'pending' | 'running' | 'waiting' | 'succeeded' | 'failed' | 'cancelled';
export type ModelTier = 'economy' | 'standard' | 'strong';
export const decompositionVersion = 'decomposition-v1';
export const standardVersion = 'analysis-architecture-v1';

export interface WorkspaceProject { projectId: string; organizationId: string | null; roles: string[]; canDecide: boolean; }
export interface RunSummary {
  runId: string; status: RunStatus; workflowVersion: string; failureCode: string | null;
  createdAt: string; updatedAt: string; pendingDecisions: number;
}
export interface WorkflowStep { ordinal: number; kind: string; status: StepStatus; attempts: number; abandoned: number; }
export interface StepDecision { id: string; ordinal: number; reason: string; options: string[]; status: 'pending' | 'decided' | 'cancelled'; choice: string | null; }
export interface UsageTotals { calls: number; inputTokens: number; outputTokens: number; estimatedCostMicros: number; unpricedCalls: number; unknownUsageCalls: number; }
export interface StepRoute { ordinal: number; attempt: number; tier: ModelTier; reason: string; policyVersion: string; provider: string | null; model: string | null; }
export interface RunPublication { workItemId: string; caseId: string; version: number; contentHash: string; }
export interface RunView {
  runId: string; projectId: string; status: RunStatus; failureCode: string | null; steps: WorkflowStep[];
  publication: RunPublication | null; decisions: StepDecision[] | null; usage: UsageTotals | null; routes: StepRoute[] | null;
}
export interface PlanPart {
  key: string; title: string; scope: string; requirementIds: string[];
  complexity: 'low' | 'medium' | 'high'; importance: 'normal' | 'high'; dependsOn: string[];
}
export interface RunPlan { runId: string; plan: { summary: string; parts: PlanPart[] }; routes: { key: string; tier: ModelTier; reason: string }[]; policyVersion: string; }
export interface ConfirmedRequirement { id: string; description: string; }
export interface RunRequest { task: string; context: string; confirmedRequirements: ConfirmedRequirement[] | null; workflowVersion: string; }
export interface RunSubmission { runId: string; status: RunStatus; replayed: boolean; }

// Verification jobs (M9.3): a generated starter application verified offline in the sandbox.
export const starterTemplate = { id: 'dotnet-angular-postgres-ddd', version: '1.0.0' } as const;
export type VerificationStatus = 'queued' | 'running' | 'passed' | 'failed' | 'timedOut' | 'sandboxError';
export interface VerificationSummary {
  jobId: string; templateId: string; templateVersion: string; applicationName: string; status: VerificationStatus;
  failureCode: string | null; attempts: number; createdAt: string; updatedAt: string;
}
export interface SandboxStepResult { name: string; outcome: 'passed' | 'failed' | 'timedOut' | 'skipped'; exitCode: number | null; duration: string; output: string; outputTruncated: boolean; }
export interface SandboxReport {
  runnerImage: string; workspaceSha256: string; outcome: 'passed' | 'failed' | 'timedOut' | 'sandboxError'; failureCode: string | null;
  startedAt: string; duration: string; steps: SandboxStepResult[];
  limits: { cpus: number; memoryMb: number; pids: number; workspaceMb: number; tempMb: number; stepTimeout: string; jobTimeout: string; outputBytes: number };
}
export interface VerificationEvidence {
  formatVersion: number; jobId: string; projectId: string; templateId: string; templateVersion: string; templateSha256: string;
  applicationName: string; manifestSha256: string; report: SandboxReport;
}
export interface VerificationView { job: VerificationSummary; evidence: VerificationEvidence | null; evidenceSha256: string | null; }
export interface VerificationSubmission { jobId: string; status: VerificationStatus; replayed: boolean; }

// Development jobs (M10.3): the Developer Agent implementing an approved review artifact version.
export type DevelopmentStatus = 'queued' | 'running' | 'verified' | 'verificationFailed' | 'notFinished' | 'budgetExhausted'
  | 'modelFailed' | 'sandboxFailed' | 'failed';
export interface DevelopmentSummary {
  jobId: string; caseId: string; artifactVersion: number; applicationName: string; status: DevelopmentStatus;
  failureCode: string | null; costMicros: number | null; createdAt: string; updatedAt: string;
}
export interface VerificationCheck { name: string; status: 'passed' | 'failed' | 'warning'; blocking: boolean; detail: string; }
export interface DevelopmentChange { path: string; kind: 'added' | 'modified' | 'deleted'; requirementIds: string[]; }
export interface DevelopmentCall { turn: number; status: string; model: string | null; inputTokens: number | null; outputTokens: number | null; reservedMicros: number; costMicros: number; }
export interface DevelopmentEvidence {
  formatVersion: number; jobId: string; caseId: string; artifactVersion: number; artifactSha256: string; templateSha256: string;
  applicationName: string; model: string; status: DevelopmentStatus; failureCode: string | null; turns: number; actions: number;
  costMicros: number; calls: DevelopmentCall[]; changes: DevelopmentChange[]; baseCommit: string | null; finalCommit: string | null;
  diff: string | null; diffSha256: string | null; verification: SandboxReport | null; log: string[];
  checks?: VerificationCheck[] | null; corrections?: number;
}
export interface PullRequestOutcome { repository: string; branch: string; status: 'Opened' | 'Failed'; number: number | null; url: string | null;
  headCommit: string | null; failureCode: string | null; createdAt: string; }
export interface DevelopmentView { job: DevelopmentSummary; evidence: DevelopmentEvidence | null; evidenceSha256: string | null; pullRequest?: PullRequestOutcome | null; }
export interface DevelopmentSubmission { jobId: string; status: DevelopmentStatus; replayed: boolean; }

// Deployments (M13A.2): a verified job's release on one of the project's targets, applied only after an
// organization Owner/Admin approved the exact plan (its binding SHA-256).
export type DeploymentStatus = 'queued' | 'planning' | 'awaitingApproval' | 'approved' | 'applying' | 'succeeded' | 'unhealthy' | 'failed'
  | 'rejected' | 'expired';
export interface DeploymentTarget { name: string; cloud: string; environment: string; application: string; location: string | null; resourceGroup: string | null; }
export interface PlannedChange { address: string; type: string; actions: string[]; }
export interface PlanSummary { add: number; change: number; destroy: number; changes: PlannedChange[]; }
export interface DeploymentOutcome {
  bindingSha256: string; url: string | null; outputs: Record<string, string> | null;
  health: { url: string; healthy: boolean; statusCode: number | null; attempts: number; duration: string } | null;
  configuration: { resolved: boolean; references: { name: string; status: string }[] } | null;
  failure: string | null; approvedBy: string | null; approvedAt: string | null;
}
export interface DeploymentView {
  deploymentId: string; targetName: string; developmentJobId: string; status: DeploymentStatus; failureCode: string | null;
  bindingSha256: string | null; releaseSha256: string | null; moduleId: string | null; moduleVersion: string | null; summary: PlanSummary | null;
  destructive: boolean | null; plannedAt: string | null; approved: boolean | null; decidedAt: string | null; allowDestructive: boolean | null;
  url: string | null; result: DeploymentOutcome | null; createdAt: string; updatedAt: string;
  binding?: Record<string, unknown> | null; planSha256?: string | null;
}
export interface DeploymentList { targets: DeploymentTarget[]; deployments: DeploymentView[]; }
export interface DeploymentSubmission { deploymentId: string; status: DeploymentStatus; replayed: boolean; }

// Review cases (M5B.2 decisions on published artifacts). The ETag is the optimistic-concurrency token.
export type ReviewState = 'pendingReview' | 'approved' | 'rejected' | 'changesRequested';
export type ReviewDecision = 'approve' | 'reject' | 'requestChanges';
export interface ReviewDecisionSummary { id: string; decision: ReviewDecision; reason: string | null; principalId: string; createdAt: string; }
export interface ReviewSnapshot {
  id: string; projectId: string; currentVersion: number; contentHash: string; revision: number;
  state: ReviewState; decision: ReviewDecisionSummary | null;
}
export interface ReviewCase { snapshot: ReviewSnapshot; etag: string; }
export interface ReviewDecisionBody { decision: ReviewDecision; artifactVersion: number; artifactSha256: string; reason: string | null; }

// Review artifact format version 1 (camelCase JSON written by the Factory).
export type RequirementStatus = 'confirmed' | 'suggested' | 'unverifiedAssumption';
export interface RequirementTrace { requirementId: string; status: RequirementStatus; }
export interface DesignBasis { isDesignRecommendation: boolean; requirements: RequirementTrace[]; assumptionDescriptions: string[]; }
export interface ReviewArtifact {
  formatVersion: number; validatorVersion: string; caseId: string; version: number; workflowId: string; createdAt: string;
  request: { projectId: string; task: string; context: string; confirmedRequirements: ConfirmedRequirement[] | null };
  analysis: {
    summary: string;
    functionalRequirements: { id: string; description: string; status: RequirementStatus; confirmedSourceId: string | null }[];
    assumptions: { description: string; validationNeeded: string }[];
    risks: { description: string; suggestedMitigation: string }[];
    openQuestions: { question: string }[];
  };
  architecture: {
    summary: string; status: 'proposed'; confirmedConstraintRequirementIds: string[];
    components: { id: string; name: string; responsibility: string; basis: DesignBasis }[];
    technologyDecisions: {
      id: string; technology: string; rationale: string; tradeOffs: string[]; basis: DesignBasis;
      alternatives: { technology: string; benefits: string; drawbacks: string }[];
    }[];
    risks: { id: string; description: string; mitigation: string; basis: DesignBasis }[];
    adrs: { id: string; technologyDecisionId: string; context: string; consequences: string }[];
  };
}

@Injectable({ providedIn: 'root' })
export class FactoryApi {
  private readonly http = inject(HttpClient);
  private readonly base = factoryConfig.apiPrefix;

  myProjects(): Observable<WorkspaceProject[]> {
    return this.http.get<WorkspaceProject[]>(`${this.base}/me/projects`);
  }

  runs(projectId: string): Observable<RunSummary[]> {
    return this.http.get<RunSummary[]>(`${this.base}/projects/${encodeURIComponent(projectId)}/runs`);
  }

  run(projectId: string, runId: string): Observable<RunView> {
    return this.http.get<RunView>(`${this.base}/projects/${encodeURIComponent(projectId)}/runs/${runId}`);
  }

  plan(projectId: string, runId: string): Observable<RunPlan> {
    return this.http.get<RunPlan>(`${this.base}/projects/${encodeURIComponent(projectId)}/runs/${runId}/plan`);
  }

  // The idempotency key is generated once per form submission so a retried request replays.
  submit(projectId: string, request: RunRequest, idempotencyKey: string): Observable<RunSubmission> {
    return this.http.post<RunSubmission>(`${this.base}/projects/${encodeURIComponent(projectId)}/runs`, request,
      { headers: new HttpHeaders({ 'Idempotency-Key': idempotencyKey }) });
  }

  decide(projectId: string, runId: string, decisionId: string, choice: string): Observable<RunView> {
    return this.http.post<RunView>(`${this.base}/projects/${encodeURIComponent(projectId)}/runs/${runId}/decisions/${decisionId}`, { choice });
  }

  cancel(projectId: string, runId: string): Observable<RunView> {
    return this.http.post<RunView>(`${this.base}/projects/${encodeURIComponent(projectId)}/runs/${runId}/cancel`, null);
  }

  verifications(projectId: string): Observable<VerificationSummary[]> {
    return this.http.get<VerificationSummary[]>(`${this.base}/projects/${encodeURIComponent(projectId)}/verifications`);
  }

  verification(projectId: string, jobId: string): Observable<VerificationView> {
    return this.http.get<VerificationView>(`${this.base}/projects/${encodeURIComponent(projectId)}/verifications/${encodeURIComponent(jobId)}`);
  }

  submitVerification(projectId: string, applicationName: string, idempotencyKey: string): Observable<VerificationSubmission> {
    return this.http.post<VerificationSubmission>(`${this.base}/projects/${encodeURIComponent(projectId)}/verifications`,
      { templateId: starterTemplate.id, templateVersion: starterTemplate.version, applicationName },
      { headers: new HttpHeaders({ 'Idempotency-Key': idempotencyKey }) });
  }

  developments(projectId: string): Observable<DevelopmentSummary[]> {
    return this.http.get<DevelopmentSummary[]>(`${this.base}/projects/${encodeURIComponent(projectId)}/developments`);
  }

  development(projectId: string, jobId: string): Observable<DevelopmentView> {
    return this.http.get<DevelopmentView>(`${this.base}/projects/${encodeURIComponent(projectId)}/developments/${encodeURIComponent(jobId)}`);
  }

  // Names the exact approved version and hash the reviewer saw; a newer version answers 412.
  submitDevelopment(projectId: string, caseId: string, artifactVersion: number, artifactSha256: string, applicationName: string,
    idempotencyKey: string): Observable<DevelopmentSubmission> {
    return this.http.post<DevelopmentSubmission>(`${this.base}/projects/${encodeURIComponent(projectId)}/developments`,
      { caseId, artifactVersion, artifactSha256, templateId: starterTemplate.id, templateVersion: starterTemplate.version, applicationName },
      { headers: new HttpHeaders({ 'Idempotency-Key': idempotencyKey }) });
  }

  deployments(projectId: string): Observable<DeploymentList> {
    return this.http.get<DeploymentList>(`${this.base}/projects/${encodeURIComponent(projectId)}/deployments`);
  }

  deployment(projectId: string, deploymentId: string): Observable<DeploymentView> {
    return this.http.get<DeploymentView>(`${this.base}/projects/${encodeURIComponent(projectId)}/deployments/${encodeURIComponent(deploymentId)}`);
  }

  submitDeployment(projectId: string, targetName: string, developmentJobId: string, idempotencyKey: string): Observable<DeploymentSubmission> {
    return this.http.post<DeploymentSubmission>(`${this.base}/projects/${encodeURIComponent(projectId)}/deployments`,
      { targetName, developmentJobId }, { headers: new HttpHeaders({ 'Idempotency-Key': idempotencyKey }) });
  }

  // Names the exact plan the approver saw; another or an expired plan answers 412.
  decideDeployment(projectId: string, deploymentId: string, approve: boolean, bindingSha256: string, allowDestructive: boolean): Observable<DeploymentView> {
    return this.http.post<DeploymentView>(
      `${this.base}/projects/${encodeURIComponent(projectId)}/deployments/${encodeURIComponent(deploymentId)}/decision`,
      { approve, bindingSha256, allowDestructive });
  }

  review(projectId: string, caseId: string): Observable<ReviewCase> {
    return this.http.get<ReviewSnapshot>(this.reviewUrl(projectId, caseId), { observe: 'response' })
      .pipe(map(r => ({ snapshot: r.body!, etag: r.headers.get('ETag') ?? '' })));
  }

  // Raw bytes, so the reviewer's view can be checked against the published SHA-256.
  reviewArtifact(projectId: string, caseId: string, version: number): Observable<ArrayBuffer> {
    return this.http.get(`${this.reviewUrl(projectId, caseId)}/artifacts/${version}`, { responseType: 'arraybuffer' });
  }

  decideReview(projectId: string, caseId: string, etag: string, idempotencyKey: string, body: ReviewDecisionBody): Observable<ReviewCase> {
    return this.http.post<ReviewSnapshot>(this.reviewUrl(projectId, caseId), body,
      { observe: 'response', headers: new HttpHeaders({ 'If-Match': etag, 'Idempotency-Key': idempotencyKey }) })
      .pipe(map(r => ({ snapshot: r.body!, etag: r.headers.get('ETag') ?? '' })));
  }

  private reviewUrl(projectId: string, caseId: string): string {
    return `${this.base}/projects/${encodeURIComponent(projectId)}/reviews/${encodeURIComponent(caseId)}`;
  }
}
