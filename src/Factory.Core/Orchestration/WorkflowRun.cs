namespace Factory.Core.Orchestration;

public enum RunStatus { Queued, Running, Succeeded, Failed }
public enum StepKind { Analysis, Architecture, Publication }
public enum StepStatus { Pending, Running, Succeeded, Failed, Cancelled }

public sealed record WorkflowStep(int Ordinal, StepKind Kind, StepStatus Status, int Attempts);

// Aggregate root for one durable run: owns the step plan and transition rules. PostgreSQL
// independently enforces the same persisted transitions; neither layer replaces the other.
// M7.1 executes steps strictly in order; at most one step of a run is Running.
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
            steps.Where((s, i) => s.Ordinal != i || s.Kind != plan[i] || s.Attempts < 0 || !Enum.IsDefined(s.Status)).Any() ||
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

    public WorkflowStep? NextRunnable() => Status is RunStatus.Queued or RunStatus.Running && _steps.All(s => s.Status != StepStatus.Running)
        ? _steps.FirstOrDefault(s => s.Status == StepStatus.Pending) : null;

    public WorkflowStep? Running => _steps.SingleOrDefault(s => s.Status == StepStatus.Running);

    public void Start(int ordinal)
    {
        if (NextRunnable()?.Ordinal != ordinal) throw new OrchestrationException(OrchestrationError.Conflict);
        Set(ordinal, StepStatus.Running, _steps[ordinal].Attempts + 1);
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

    // A lost lease is not a model failure: the step may run again until its attempt bound.
    // Re-execution can repeat an external provider call; nothing here is exactly-once.
    public void Abandon(int ordinal)
    {
        RequireRunning(ordinal);
        if (_steps[ordinal].Attempts < MaxAttempts(_steps[ordinal].Kind)) Set(ordinal, StepStatus.Pending);
        else Fail(ordinal, "step_attempts_exhausted");
    }

    private void RequireRunning(int ordinal)
    {
        if (ordinal < 0 || ordinal >= _steps.Length || _steps[ordinal].Status != StepStatus.Running)
            throw new OrchestrationException(OrchestrationError.Conflict);
    }

    private void Set(int ordinal, StepStatus status, int? attempts = null)
        => _steps[ordinal] = _steps[ordinal] with { Status = status, Attempts = attempts ?? _steps[ordinal].Attempts };

    private bool Consistent()
    {
        var firstOpen = Array.FindIndex(_steps, s => s.Status != StepStatus.Succeeded);
        var prefixDone = _steps.Select((s, i) => s.Status is StepStatus.Pending or StepStatus.Cancelled ||
            _steps.Take(i).All(p => p.Status == StepStatus.Succeeded)).All(x => x);
        return prefixDone && _steps.Count(s => s.Status == StepStatus.Running) <= 1 &&
            _steps.All(s => s.Status == StepStatus.Pending ? true : s.Attempts > 0 || s.Status == StepStatus.Cancelled) &&
            Status switch
            {
                RunStatus.Queued => _steps.All(s => s.Status == StepStatus.Pending && s.Attempts == 0),
                RunStatus.Running => firstOpen >= 0 && _steps.All(s => s.Status is not (StepStatus.Failed or StepStatus.Cancelled)),
                RunStatus.Succeeded => firstOpen < 0,
                RunStatus.Failed => _steps.Count(s => s.Status == StepStatus.Failed) == 1 &&
                    _steps.All(s => s.Status is not (StepStatus.Pending or StepStatus.Running)),
                _ => false
            };
    }
}
