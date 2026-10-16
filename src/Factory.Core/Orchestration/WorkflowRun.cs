using Factory.Core.Decomposition;

namespace Factory.Core.Orchestration;

public enum RunStatus { Queued, Running, Waiting, Succeeded, Failed, Cancelled }
// Analysis, Architecture and Publication keep ordinals 0, 1 and 2 in every workflow version, so the
// review artifact and publication stay unchanged. Decomposition adds Planning (3), PlanApproval (4)
// and Part steps (5..). In decomposition-v1 the Architecture step merges the part proposals.
public enum StepKind { Analysis, Architecture, Publication, Planning, PlanApproval, Part }
public enum StepStatus { Pending, Running, Waiting, Succeeded, Failed, Cancelled }

// Abandoned counts lost leases only; a suspended (waiting) attempt does not consume the bound.
public sealed record WorkflowStep(int Ordinal, StepKind Kind, StepStatus Status, int Attempts, int Abandoned = 0);

// Ordinal runs only after DependsOn succeeded. Explicit in decomposition-v1; implicit (every lower
// ordinal) in analysis-architecture-v1.
public sealed record StepDependency(int Ordinal, int DependsOn);

public static class WorkflowVersions
{
    public const string AnalysisArchitecture = "analysis-architecture-v1";
    public const string Decomposition = "decomposition-v1";
    public const int FirstPartOrdinal = 5;
    // Parallel model attempts of one decomposition run; the database enforces the same bound.
    public const int MaxConcurrentAttempts = 4;
    public static bool IsKnown(string? version) => version is AnalysisArchitecture or Decomposition;
}

// Aggregate root for one durable run: owns the step plan and transition rules. PostgreSQL
// independently enforces the same persisted transitions; neither layer replaces the other.
// analysis-architecture-v1 runs strictly in order, one step at a time. decomposition-v1 runs a
// dependency graph: independent parts run in parallel (bounded), each writing only its own step.
public sealed class WorkflowRun
{
    private readonly List<WorkflowStep> _steps;
    private readonly List<StepDependency> _dependencies;
    public Guid Id { get; }
    public string Version { get; }
    public RunStatus Status { get; private set; }
    public string? FailureCode { get; private set; }
    public IReadOnlyList<WorkflowStep> Steps => _steps;
    public IReadOnlyList<StepDependency> Dependencies => _dependencies;
    private bool Decomposed => Version == WorkflowVersions.Decomposition;

    private WorkflowRun(Guid id, string version, RunStatus status, string? failure, IEnumerable<WorkflowStep> steps, IEnumerable<StepDependency> dependencies)
    {
        if (id == Guid.Empty || !WorkflowVersions.IsKnown(version) || !Enum.IsDefined(status) || (status == RunStatus.Failed) != failure is not null)
            throw new OrchestrationException(OrchestrationError.Integrity);
        Id = id; Version = version; Status = status; FailureCode = failure;
        _steps = steps.OrderBy(s => s.Ordinal).ToList();
        _dependencies = dependencies.Distinct().ToList();
        if (!Consistent()) throw new OrchestrationException(OrchestrationError.Integrity);
    }

    public static IReadOnlyList<StepKind> Plan(string workflowVersion) => workflowVersion switch
    {
        WorkflowVersions.AnalysisArchitecture => [StepKind.Analysis, StepKind.Architecture, StepKind.Publication],
        WorkflowVersions.Decomposition => [StepKind.Analysis, StepKind.Architecture, StepKind.Publication, StepKind.Planning, StepKind.PlanApproval],
        _ => throw new OrchestrationException(OrchestrationError.InvalidCommand)
    };

    // Initial edges of decomposition-v1: Analysis → Planning → PlanApproval → Architecture → Publication.
    // Approving the plan adds the parts between PlanApproval and Architecture.
    public static IReadOnlyList<StepDependency> InitialDependencies(string workflowVersion) => workflowVersion == WorkflowVersions.Decomposition
        ? [new(3, 0), new(4, 3), new(1, 4), new(2, 1)]
        : WorkflowVersions.IsKnown(workflowVersion) ? [] : throw new OrchestrationException(OrchestrationError.InvalidCommand);

    public static WorkflowRun Create(Guid id, string workflowVersion)
        => new(id, workflowVersion, RunStatus.Queued, null,
            Plan(workflowVersion).Select((kind, i) => new WorkflowStep(i, kind, StepStatus.Pending, 0)), InitialDependencies(workflowVersion));

    public static WorkflowRun Restore(Guid id, RunStatus status, string? failureCode, IEnumerable<WorkflowStep> steps)
        => new(id, WorkflowVersions.AnalysisArchitecture, status, failureCode, steps, []);

    public static WorkflowRun Restore(Guid id, string version, RunStatus status, string? failureCode, IEnumerable<WorkflowStep> steps,
        IEnumerable<StepDependency> dependencies)
        => new(id, version, status, failureCode, steps, dependencies);

    public static int MaxAttempts(StepKind kind) => kind == StepKind.Publication
        ? DurableWorkflowLimits.MaxPublicationAttempts : DurableWorkflowLimits.MaxModelStepAttempts;

    public bool IsTerminal => Status is RunStatus.Succeeded or RunStatus.Failed or RunStatus.Cancelled;
    private int Concurrency => Decomposed ? WorkflowVersions.MaxConcurrentAttempts : 1;

    public IEnumerable<int> DependenciesOf(int ordinal) => Decomposed
        ? _dependencies.Where(d => d.Ordinal == ordinal).Select(d => d.DependsOn)
        : Enumerable.Range(0, Math.Max(0, ordinal));

    private bool Ready(WorkflowStep step) => step.Status == StepStatus.Pending &&
        DependenciesOf(step.Ordinal).All(d => _steps[d].Status == StepStatus.Succeeded);

    // Steps a worker may start now, in ordinal order.
    public IReadOnlyList<WorkflowStep> Runnable()
    {
        if (Status is not (RunStatus.Queued or RunStatus.Running) || _steps.Count(s => s.Status == StepStatus.Running) >= Concurrency)
            return [];
        if (!Decomposed && _steps.Any(s => s.Status == StepStatus.Waiting)) return [];
        return _steps.Where(Ready).ToList();
    }

    public WorkflowStep? NextRunnable() => Runnable().FirstOrDefault();

    // The single running step of an analysis-architecture-v1 run (null when none).
    public WorkflowStep? Running => _steps.SingleOrDefault(s => s.Status == StepStatus.Running);
    public bool IsRunning(int ordinal) => ordinal >= 0 && ordinal < _steps.Count && _steps[ordinal].Status == StepStatus.Running;

    public void Start(int ordinal)
    {
        if (!Runnable().Any(s => s.Ordinal == ordinal)) throw new OrchestrationException(OrchestrationError.Conflict);
        Set(ordinal, StepStatus.Running, attempts: _steps[ordinal].Attempts + 1);
        Status = RunStatus.Running;
    }

    public void Succeed(int ordinal)
    {
        RequireRunning(ordinal);
        if (_steps[ordinal].Kind == StepKind.PlanApproval) throw new OrchestrationException(OrchestrationError.InvalidCommand);
        Set(ordinal, StepStatus.Succeeded);
        if (_steps.All(s => s.Status == StepStatus.Succeeded)) Status = RunStatus.Succeeded;
    }

    // Completes plan approval and adds one Part step per approved part, atomically: the merge step
    // can never become runnable between approval and the parts' creation.
    public void ApprovePlan(int ordinal, DecompositionPlan plan)
    {
        RequireRunning(ordinal);
        if (!Decomposed || _steps[ordinal].Kind != StepKind.PlanApproval || _steps.Any(s => s.Kind == StepKind.Part) ||
            DecompositionPlanValidator.GetStructureError(plan) is not null)
            throw new OrchestrationException(OrchestrationError.InvalidCommand);
        Set(ordinal, StepStatus.Succeeded);
        for (var i = 0; i < plan.Parts.Count; i++)
        {
            var partOrdinal = WorkflowVersions.FirstPartOrdinal + i;
            _steps.Add(new WorkflowStep(partOrdinal, StepKind.Part, StepStatus.Pending, 0));
            _dependencies.Add(new StepDependency(partOrdinal, 4));
            foreach (var key in plan.Parts[i].DependsOn)
                _dependencies.Add(new StepDependency(partOrdinal, WorkflowVersions.FirstPartOrdinal + DecompositionPlan.Index(key)));
            _dependencies.Add(new StepDependency(1, partOrdinal));
        }
        if (!Consistent()) throw new OrchestrationException(OrchestrationError.Integrity);
    }

    // Ends the run: other active steps are cancelled (a sibling's lease is lost at its next write).
    public void Fail(int ordinal, string failureCode)
    {
        RequireRunning(ordinal);
        if (string.IsNullOrWhiteSpace(failureCode)) throw new OrchestrationException(OrchestrationError.InvalidCommand);
        Set(ordinal, StepStatus.Failed);
        foreach (var other in _steps.Where(s => s.Status is StepStatus.Pending or StepStatus.Running or StepStatus.Waiting).ToList())
            Set(other.Ordinal, StepStatus.Cancelled);
        Status = RunStatus.Failed; FailureCode = failureCode;
    }

    // A lost lease is not a model failure: the step may run again until its lost-lease bound.
    // Re-execution can repeat an external provider call; nothing here is exactly-once.
    public void Abandon(int ordinal)
    {
        RequireRunning(ordinal);
        var abandoned = _steps[ordinal].Abandoned + 1;
        Set(ordinal, StepStatus.Running, abandoned: abandoned);
        if (abandoned < MaxAttempts(_steps[ordinal].Kind)) Set(ordinal, StepStatus.Pending);
        else Fail(ordinal, "step_attempts_exhausted");
    }

    // The step pauses for a human decision without holding a lease or a timeout. In decomposition-v1
    // running siblings may finish meanwhile, but nothing new starts until every decision is made.
    public void Suspend(int ordinal)
    {
        RequireRunning(ordinal);
        if (_steps[ordinal].Kind is StepKind.Publication) throw new OrchestrationException(OrchestrationError.InvalidCommand);
        Set(ordinal, StepStatus.Waiting);
        Status = RunStatus.Waiting;
    }

    public void Resume(int ordinal)
    {
        if (Status != RunStatus.Waiting || ordinal < 0 || ordinal >= _steps.Count || _steps[ordinal].Status != StepStatus.Waiting)
            throw new OrchestrationException(OrchestrationError.Conflict);
        Set(ordinal, StepStatus.Pending);
        if (!_steps.Any(s => s.Status == StepStatus.Waiting)) Status = RunStatus.Running;
    }

    // Cancelling an already cancelled run is a no-op; finished runs cannot be cancelled.
    public void Cancel()
    {
        if (Status == RunStatus.Cancelled) return;
        if (IsTerminal) throw new OrchestrationException(OrchestrationError.Conflict);
        foreach (var step in _steps.Where(s => s.Status is StepStatus.Pending or StepStatus.Running or StepStatus.Waiting).ToList())
            Set(step.Ordinal, StepStatus.Cancelled);
        Status = RunStatus.Cancelled;
    }

    private void RequireRunning(int ordinal)
    {
        if (!IsRunning(ordinal)) throw new OrchestrationException(OrchestrationError.Conflict);
    }

    private void Set(int ordinal, StepStatus status, int? attempts = null, int? abandoned = null)
        => _steps[ordinal] = _steps[ordinal] with { Status = status, Attempts = attempts ?? _steps[ordinal].Attempts,
            Abandoned = abandoned ?? _steps[ordinal].Abandoned };

    private bool Consistent()
    {
        bool Any(params StepStatus[] statuses) => _steps.Any(s => statuses.Contains(s.Status));
        var plan = Plan(Version);
        var parts = _steps.Skip(plan.Count).ToList();
        if (_steps.Count < plan.Count || _steps.Where((s, i) => s.Ordinal != i || s.Attempts < 0 || s.Abandoned < 0 ||
                s.Abandoned > s.Attempts || !Enum.IsDefined(s.Status) || s.Kind != (i < plan.Count ? plan[i] : StepKind.Part)).Any())
            return false;
        if (Decomposed)
        {
            // Parts exist only after approval, at most twelve, each feeding the merge step.
            if (parts.Count > DecompositionPlan.MaxParts || (parts.Count > 0) != (_steps[4].Status == StepStatus.Succeeded) ||
                _dependencies.Any(d => d.Ordinal < 0 || d.Ordinal >= _steps.Count || d.DependsOn < 0 || d.DependsOn >= _steps.Count || d.Ordinal == d.DependsOn ||
                    (d.Ordinal >= WorkflowVersions.FirstPartOrdinal && d.DependsOn >= d.Ordinal)) ||
                !InitialDependencies(Version).All(_dependencies.Contains) ||
                !parts.All(p => _dependencies.Contains(new(1, p.Ordinal)) && _dependencies.Contains(new(p.Ordinal, 4))) ||
                _dependencies.Count(d => d.Ordinal < WorkflowVersions.FirstPartOrdinal) != 4 + parts.Count)
                return false;
        }
        else if (_steps.Count != plan.Count || _dependencies.Count > 0) return false;
        var ordered = _steps.All(s => s.Status is StepStatus.Pending or StepStatus.Cancelled ||
            DependenciesOf(s.Ordinal).All(d => _steps[d].Status == StepStatus.Succeeded));
        var running = _steps.Count(s => s.Status == StepStatus.Running);
        var waiting = _steps.Count(s => s.Status == StepStatus.Waiting);
        return ordered && running <= Concurrency && (Decomposed || running + waiting <= 1) &&
            _steps.All(s => s.Status is StepStatus.Pending or StepStatus.Cancelled || s.Attempts > 0) &&
            Status switch
            {
                RunStatus.Queued => _steps.All(s => s.Status == StepStatus.Pending && s.Attempts == 0),
                RunStatus.Running => !Any(StepStatus.Waiting, StepStatus.Failed, StepStatus.Cancelled) && Any(StepStatus.Pending, StepStatus.Running),
                RunStatus.Waiting => waiting >= 1 && !Any(StepStatus.Failed, StepStatus.Cancelled) && (Decomposed || running == 0),
                RunStatus.Succeeded => _steps.All(s => s.Status == StepStatus.Succeeded),
                RunStatus.Failed => _steps.Count(s => s.Status == StepStatus.Failed) == 1 && !Any(StepStatus.Pending, StepStatus.Running, StepStatus.Waiting),
                RunStatus.Cancelled => !Any(StepStatus.Pending, StepStatus.Running, StepStatus.Waiting, StepStatus.Failed),
                _ => false
            };
    }
}
