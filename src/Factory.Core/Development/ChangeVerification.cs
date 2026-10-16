using System.Text.RegularExpressions;
using Factory.Core.Execution;
using Factory.Core.Templates;

namespace Factory.Core.Development;

// M11.1: what the Factory itself establishes about a change, separate from what the agent claims.
// Sandbox outcomes come from the Docker daemon; the test report is read by a Factory command right
// after the tests; layer, protection and traceability checks run on the authoritative files without
// executing anything. A blocking failure means the change is not verified.
public enum CheckStatus { Passed, Failed, Warning }

public sealed record VerificationCheck(string Name, CheckStatus Status, bool Blocking, string Detail);

public sealed record TestReport(int Total, int Passed, int Failed, IReadOnlyDictionary<string, string> Outcomes);

public sealed record ChangeVerdict(bool Verified, IReadOnlyList<VerificationCheck> Checks, TestReport? Tests, string? AgentClaim)
{
    public string? FailureCode => Verified ? null : "check_" + Checks.First(c => c.Blocking && c.Status == CheckStatus.Failed).Name;

    // Short, model-facing description of what must be fixed.
    public string Findings => string.Join('\n', Checks.Where(c => c.Status != CheckStatus.Passed)
        .Select(c => $"{(c.Blocking && c.Status == CheckStatus.Failed ? "BLOCKING" : "warning")} {c.Name}: {c.Detail}"));
}

public static partial class ChangeChecks
{
    public const string TestReportStep = "test-report";
    public static readonly string[] RequiredTests =
        ["ArchitectureTests.Domain_DependsOnlyOnTheBaseClassLibrary", "ArchitectureTests.Application_DependsOnlyOnDomain",
         "ArchitectureTests.Infrastructure_DoesNotDependOnTheApi"];

    // Parses the test-report step: the TRX Counters element, then testName/outcome attribute pairs.
    public static TestReport? ParseTestReport(string output)
    {
        var counters = Counters().Match(output);
        if (!counters.Success) return null;
        var outcomes = new Dictionary<string, string>(StringComparer.Ordinal);
        string? name = null;
        foreach (Match m in Attribute().Matches(output))
        {
            if (m.Groups[1].Value == "testName") name = m.Groups[2].Value;
            else if (name is not null) { outcomes[name] = m.Groups[2].Value; name = null; }
        }
        return new(int.Parse(counters.Groups[1].Value), int.Parse(counters.Groups[2].Value), int.Parse(counters.Groups[3].Value), outcomes);
    }

    public static ChangeVerdict Evaluate(DevelopmentTask task, DevelopmentWorkspace workspace, SandboxReport sandbox, string? agentClaim)
    {
        var name = workspace.Name.Value;
        var checks = new List<VerificationCheck>();
        var failedStep = sandbox.Steps.FirstOrDefault(s => s.Outcome != StepOutcome.Passed);
        checks.Add(new("sandbox", sandbox.Outcome == SandboxOutcome.Passed ? CheckStatus.Passed : CheckStatus.Failed, true,
            failedStep is null ? "all offline steps passed" : $"{failedStep.Name} {failedStep.Outcome.ToString().ToLowerInvariant()} (exit {failedStep.ExitCode?.ToString() ?? "none"})"));

        var report = sandbox.Steps.FirstOrDefault(s => s.Name == TestReportStep && s.Outcome == StepOutcome.Passed) is { } step
            ? ParseTestReport(step.Output) : null;
        var missing = report is null ? RequiredTests.ToList()
            : RequiredTests.Where(t => !report.Outcomes.TryGetValue($"{name}.Tests.{t}", out var o) || o != "Passed").ToList();
        checks.Add(new("required_tests", report is not null && report.Failed == 0 && missing.Count == 0 ? CheckStatus.Passed : CheckStatus.Failed, true,
            report is null ? "no test report" : missing.Count > 0 ? "not passed: " + string.Join(", ", missing)
                : $"{report.Passed} of {report.Total} tests passed, architecture tests included"));

        var violations = LayerViolations(workspace.Name, workspace.Files);
        checks.Add(new("layers", violations.Count == 0 ? CheckStatus.Passed : CheckStatus.Failed, true,
            violations.Count == 0 ? "no forbidden references between layers" : string.Join("; ", violations.Take(10))));

        var baseFiles = workspace.BaseFiles.ToDictionary(f => f.Path, f => f.Content, StringComparer.Ordinal);
        var current = workspace.Files.ToDictionary(f => f.Path, f => f.Content, StringComparer.Ordinal);
        var tampered = baseFiles.Keys.Union(current.Keys).Where(p => WorkspaceRules.IsProtected(p, workspace.Name) &&
            (!baseFiles.TryGetValue(p, out var b) || !current.TryGetValue(p, out var c) || b != c)).ToList();
        checks.Add(new("protected_files", tampered.Count == 0 ? CheckStatus.Passed : CheckStatus.Failed, true,
            tampered.Count == 0 ? "solution, projects, build settings, packages and architecture tests unchanged" : string.Join(", ", tampered)));

        var changes = workspace.Changes();
        var covered = changes.SelectMany(c => c.RequirementIds).ToHashSet(StringComparer.Ordinal);
        var uncovered = task.RequirementIds.Where(id => !covered.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();
        checks.Add(new("requirement_coverage", uncovered.Count == 0 ? CheckStatus.Passed : CheckStatus.Warning, false,
            uncovered.Count == 0 ? "every approved requirement is referenced by a change" : "no change references " + string.Join(", ", uncovered)));

        var addedTests = changes.Any(c => c.Kind != FileChangeKind.Deleted && c.Path.StartsWith($"tests/{name}.Tests/", StringComparison.Ordinal));
        checks.Add(new("new_tests", addedTests ? CheckStatus.Passed : CheckStatus.Warning, false,
            addedTests ? "the change adds or updates tests" : "the change adds no tests"));

        return new(checks.All(c => !c.Blocking || c.Status == CheckStatus.Passed), checks, report, agentClaim);
    }

    // Text scan of the authoritative sources: the domain references no framework or other layer; the
    // application no persistence, transport or outer layer; infrastructure not the Api.
    public static IReadOnlyList<string> LayerViolations(ApplicationName name, IReadOnlyList<TemplateFile> files)
    {
        var rules = new (string Prefix, string[] Forbidden)[]
        {
            ($"src/{name.Value}.Domain/", ["Microsoft.", "Npgsql", "System.Data", $"{name.Value}.Application", $"{name.Value}.Infrastructure", $"{name.Value}.Api"]),
            ($"src/{name.Value}.Application/", ["Microsoft.EntityFrameworkCore", "Npgsql", "Microsoft.AspNetCore", $"{name.Value}.Infrastructure", $"{name.Value}.Api"]),
            ($"src/{name.Value}.Infrastructure/", ["Microsoft.AspNetCore", $"{name.Value}.Api"]),
        };
        var violations = new List<string>();
        foreach (var file in files.Where(f => f.Path.EndsWith(".cs", StringComparison.Ordinal)))
            foreach (var (prefix, forbidden) in rules.Where(r => file.Path.StartsWith(r.Prefix, StringComparison.Ordinal)))
            {
                var patterns = forbidden.Select(f => (Name: f, Pattern: Reference(f))).ToList();
                foreach (var line in file.Content.Split('\n').Select(l => l.Trim()).Where(l => !l.StartsWith("//", StringComparison.Ordinal)))
                    foreach (var (reference, _) in patterns.Where(p => p.Pattern.IsMatch(line)))
                        violations.Add($"{file.Path} references {reference}");
            }
        return violations.Distinct().ToList();
    }

    // Whole-name match; a name ending in '.' (a namespace prefix) matches anything that follows it.
    private static Regex Reference(string name) => new(@"(?<![A-Za-z0-9_])" + Regex.Escape(name) +
        (name.EndsWith('.') ? "" : @"(?![A-Za-z0-9_])"), RegexOptions.CultureInvariant);

    [GeneratedRegex("<Counters total=\"(\\d+)\" executed=\"\\d+\" passed=\"(\\d+)\" failed=\"(\\d+)\"")]
    private static partial Regex Counters();

    [GeneratedRegex("(testName|outcome)=\"([^\"]*)\"")]
    private static partial Regex Attribute();
}
