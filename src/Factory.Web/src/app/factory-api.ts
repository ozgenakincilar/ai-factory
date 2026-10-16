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
