namespace Factory.Core.Orchestration;

public enum RunStatus { Queued, Running, Waiting, Succeeded, Failed, Cancelled }
public enum StepKind { Analysis, Architecture, Publication }
public enum StepStatus { Pending, Running, Waiting, Succeeded, Failed, Cancelled }

// Abandoned counts lost leases only; a suspended (waiting) attempt does not consume the bound.
public sealed record WorkflowStep(int Ordinal, StepKind Kind, StepStatus Status, int Attempts, int Abandoned = 0);

// Aggregate root for one durable run: owns the step plan and transition rules. PostgreSQL
// independently enforces the same persisted transitions; neither layer replaces the other.
// Steps execute strictly in order; at most one step is Running or Waiting. Parallel parts
// arrive with M8 request decomposition (see DECISIONS.md).
public sealed class WorkflowRun
{
    private readonly WorkflowStep[] _steps;
    public Guid Id { get; }
    public RunStatus Status { get; private set; }
    public string? FailureCode { get; private set; }
    public IReadOnlyList<WorkflowStep> Steps => _steps;

    private WorkflowRun(Guid id, RunStatus status, string? failure, WorkflowStep[] steps)
    {
        var plan = Plan(DurableWorkflowLimits.WorkflowVersion);
        if (id == Guid.Empty || !Enum.IsDefined(status) || steps.Length != plan.Count ||
            steps.Where((s, i) => s.Ordinal != i || s.Kind != plan[i] || s.Attempts < 0 || s.Abandoned < 0 ||
                s.Abandoned > s.Attempts || !Enum.IsDefined(s.Status)).Any() ||
            (status == RunStatus.Failed) != failure is not null)
            throw new OrchestrationException(OrchestrationError.Integrity);
        Id = id; Status = status; FailureCode = failure; _steps = steps;
        if (!Consistent()) throw new OrchestrationException(OrchestrationError.Integrity);
    }

    public static IReadOnlyList<StepKind> Plan(string workflowVersion) => workflowVersion == DurableWorkflowLimits.WorkflowVersion
        ? [StepKind.Analysis, StepKind.Architecture, StepKind.Publication]
        : throw new OrchestrationException(OrchestrationError.InvalidCommand);

    public static WorkflowRun Create(Guid id, string workflowVersion)
        => new(id, RunStatus.Queued, null, Plan(workflowVersion).Select((kind, i) => new WorkflowStep(i, kind, StepStatus.Pending, 0)).ToArray());

    public static WorkflowRun Restore(Guid id, RunStatus status, string? failureCode, IEnumerable<WorkflowStep> steps)
        => new(id, status, failureCode, steps.OrderBy(s => s.Ordinal).ToArray());

    public static int MaxAttempts(StepKind kind) => kind == StepKind.Publication
        ? DurableWorkflowLimits.MaxPublicationAttempts : DurableWorkflowLimits.MaxModelStepAttempts;

    public bool IsTerminal => Status is RunStatus.Succeeded or RunStatus.Failed or RunStatus.Cancelled;

    public WorkflowStep? NextRunnable() => Status is RunStatus.Queued or RunStatus.Running &&
        _steps.All(s => s.Status is not (StepStatus.Running or StepStatus.Waiting))
        ? _steps.FirstOrDefault(s => s.Status == StepStatus.Pending) : null;

    public WorkflowStep? Running => _steps.SingleOrDefault(s => s.Status == StepStatus.Running);

    public void Start(int ordinal)
    {
        if (NextRunnable()?.Ordinal != ordinal) throw new OrchestrationException(OrchestrationError.Conflict);
        Set(ordinal, StepStatus.Running, attempts: _steps[ordinal].Attempts + 1);
        Status = RunStatus.Running;
    }

    public void Succeed(int ordinal)
    {
        RequireRunning(ordinal);
        Set(ordinal, StepStatus.Succeeded);
        if (_steps.All(s => s.Status == StepStatus.Succeeded)) Status = RunStatus.Succeeded;
    }

    public void Fail(int ordinal, string failureCode)
    {
        RequireRunning(ordinal);
        if (string.IsNullOrWhiteSpace(failureCode)) throw new OrchestrationException(OrchestrationError.InvalidCommand);
        Set(ordinal, StepStatus.Failed);
        foreach (var later in _steps.Where(s => s.Ordinal > ordinal)) Set(later.Ordinal, StepStatus.Cancelled);
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

    // The step pauses for a human decision without holding a lease or a timeout.
    public void Suspend(int ordinal)
    {
        RequireRunning(ordinal);
        if (_steps[ordinal].Kind == StepKind.Publication) throw new OrchestrationException(OrchestrationError.InvalidCommand);
        Set(ordinal, StepStatus.Waiting);
        Status = RunStatus.Waiting;
    }

    public void Resume(int ordinal)
    {
        if (Status != RunStatus.Waiting || ordinal < 0 || ordinal >= _steps.Length || _steps[ordinal].Status != StepStatus.Waiting)
            throw new OrchestrationException(OrchestrationError.Conflict);
        Set(ordinal, StepStatus.Pending);
        Status = RunStatus.Running;
    }

    // Cancelling an already cancelled run is a no-op; finished runs cannot be cancelled.
    public void Cancel()
    {
        if (Status == RunStatus.Cancelled) return;
        if (IsTerminal) throw new OrchestrationException(OrchestrationError.Conflict);
        foreach (var step in _steps.Where(s => s.Status is StepStatus.Pending or StepStatus.Running or StepStatus.Waiting))
            Set(step.Ordinal, StepStatus.Cancelled);
        Status = RunStatus.Cancelled;
    }

    private void RequireRunning(int ordinal)
    {
        if (ordinal < 0 || ordinal >= _steps.Length || _steps[ordinal].Status != StepStatus.Running)
            throw new OrchestrationException(OrchestrationError.Conflict);
    }

    private void Set(int ordinal, StepStatus status, int? attempts = null, int? abandoned = null)
        => _steps[ordinal] = _steps[ordinal] with { Status = status, Attempts = attempts ?? _steps[ordinal].Attempts,
            Abandoned = abandoned ?? _steps[ordinal].Abandoned };

    private bool Consistent()
    {
        bool Any(params StepStatus[] statuses) => _steps.Any(s => statuses.Contains(s.Status));
        var ordered = _steps.Select((s, i) => s.Status is StepStatus.Pending or StepStatus.Cancelled ||
            _steps.Take(i).All(p => p.Status == StepStatus.Succeeded)).All(x => x);
        var active = _steps.Count(s => s.Status is StepStatus.Running or StepStatus.Waiting);
        return ordered && active <= 1 &&
            _steps.All(s => s.Status is StepStatus.Pending or StepStatus.Cancelled || s.Attempts > 0) &&
            Status switch
            {
                RunStatus.Queued => _steps.All(s => s.Status == StepStatus.Pending && s.Attempts == 0),
                RunStatus.Running => !Any(StepStatus.Waiting, StepStatus.Failed, StepStatus.Cancelled) && Any(StepStatus.Pending, StepStatus.Running),
                RunStatus.Waiting => _steps.Count(s => s.Status == StepStatus.Waiting) == 1 && !Any(StepStatus.Running, StepStatus.Failed, StepStatus.Cancelled),
                RunStatus.Succeeded => _steps.All(s => s.Status == StepStatus.Succeeded),
                RunStatus.Failed => _steps.Count(s => s.Status == StepStatus.Failed) == 1 && !Any(StepStatus.Pending, StepStatus.Running, StepStatus.Waiting),
                RunStatus.Cancelled => !Any(StepStatus.Pending, StepStatus.Running, StepStatus.Waiting, StepStatus.Failed),
                _ => false
            };
    }
}
