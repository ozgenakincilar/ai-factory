using Factory.Core.Development;
using Factory.Core.Execution;
using Factory.Core.Templates;

namespace Factory.Tests.Templates;

public class ChangeVerificationTests
{
    private static readonly ApplicationName Name = ApplicationName.Create("SampleShop");

    [Fact]
    public void TheTestReport_IsParsedFromTrxCountersAndAttributes()
    {
        var report = ChangeChecks.ParseTestReport(DeveloperScripts.TestReportOutput("SampleShop", 1))!;
        Assert.Equal((7, 7, 0), (report.Total, report.Passed, report.Failed));
        Assert.Equal("Passed", report.Outcomes["SampleShop.Tests.ArchitectureTests.Application_DependsOnlyOnDomain"]);
        Assert.Null(ChangeChecks.ParseTestReport("no report here"));
    }

    [Fact]
    public void LayerViolations_AreFoundInTheAuthoritativeSources_ButNotInComments()
    {
        TemplateFile F(string path, string content) => new(path, content);
        var violations = ChangeChecks.LayerViolations(Name,
        [
            F("src/SampleShop.Domain/Products/Product.cs", "using Microsoft.EntityFrameworkCore;\nnamespace SampleShop.Domain.Products;"),
            F("src/SampleShop.Domain/Products/Rules.cs", "// Microsoft. is mentioned only in a comment\nnamespace SampleShop.Domain;"),
            F("src/SampleShop.Domain/Orders/Order.cs", "var x = new SampleShop.Infrastructure.Persistence.AppDbContext(null!);"),
            F("src/SampleShop.Application/Products/Service.cs", "using SampleShop.Infrastructure.Persistence;"),
            F("src/SampleShop.Application/Products/Ok.cs", "using SampleShop.Domain.Products;\nusing Microsoft.Extensions.DependencyInjection;"),
            F("src/SampleShop.Infrastructure/Persistence/Repo.cs", "using Microsoft.AspNetCore.Http;"),
            F("src/SampleShop.Api/Program.cs", "using SampleShop.Infrastructure;"),
            F("src/SampleShop.Domain/SampleShopDomainNotes.md", "Microsoft.EntityFrameworkCore"),
        ]);
        Assert.Equal(
        [
            "src/SampleShop.Domain/Products/Product.cs references Microsoft.",
            "src/SampleShop.Domain/Orders/Order.cs references SampleShop.Infrastructure",
            "src/SampleShop.Application/Products/Service.cs references SampleShop.Infrastructure",
            "src/SampleShop.Infrastructure/Persistence/Repo.cs references Microsoft.AspNetCore",
        ], violations);
    }

    private static DevelopmentWorkspace Workspace(params (string Path, string Content)[] writes) => Workspace(["FR-1"], writes);

    private static DevelopmentWorkspace Workspace(string[] requirements, params (string Path, string Content)[] writes)
    {
        var workspace = new DevelopmentWorkspace(DevelopmentWorkspaceTests.Application("SampleShop"));
        foreach (var (path, content) in writes) workspace.Write(path, content, requirements);
        return workspace;
    }

    private static SandboxReport Report(DevelopmentWorkspace workspace, string testReport) =>
        new("sha256:" + new string('a', 64), new string('b', 64), SandboxLimits.Default,
            [new StepResult("dotnet-test", StepOutcome.Passed, 0, TimeSpan.Zero, "", false),
             new StepResult(ChangeChecks.TestReportStep, StepOutcome.Passed, 0, TimeSpan.Zero, testReport, false)],
            SandboxOutcome.Passed, null, DateTimeOffset.UnixEpoch, TimeSpan.Zero);

    [Fact]
    public void AGreenSandbox_IsNotEnough_WhenArchitectureTestsDidNotRunOrPass()
    {
        var workspace = Workspace(("tests/SampleShop.Tests/NoteTests.cs", "namespace SampleShop.Tests;"));
        // Exit code 0 but the report lacks the architecture tests (for example tests skipped by tampering).
        var skipped = ChangeChecks.Evaluate(DeveloperScripts.Task, workspace,
            Report(workspace, "<Counters total=\"1\" executed=\"1\" passed=\"1\" failed=\"0\" />\ntestName=\"SampleShop.Tests.NoteTests.A\"\noutcome=\"Passed\""), "all green");
        Assert.False(skipped.Verified);
        Assert.Equal("check_required_tests", skipped.FailureCode);
        Assert.Contains("not passed: ArchitectureTests.Domain_DependsOnlyOnTheBaseClassLibrary", skipped.Findings);
        var failing = ChangeChecks.Evaluate(DeveloperScripts.Task, workspace, Report(workspace, DeveloperScripts.TestReportOutput("SampleShop", 0, "Failed")), null);
        Assert.Equal("check_required_tests", failing.FailureCode);
        var missing = ChangeChecks.Evaluate(DeveloperScripts.Task, workspace, Report(workspace, "garbage"), null);
        Assert.Contains(missing.Checks, c => c.Name == "required_tests" && c.Detail == "no test report");
    }

    [Fact]
    public void LayerViolations_AndUncoveredRequirementsBlock_MissingTestsAreAWarning()
    {
        var violating = Workspace(("src/SampleShop.Domain/Products/Product.cs", "using Microsoft.EntityFrameworkCore;\nnamespace SampleShop.Domain.Products;"));
        var verdict = ChangeChecks.Evaluate(DeveloperScripts.Task, violating, Report(violating, DeveloperScripts.TestReportOutput("SampleShop")), null);
        Assert.Equal("check_layers", verdict.FailureCode);
        // FR-2 is only a suggestion and never referenced: a warning. No tests were added: a warning.
        Assert.Contains(verdict.Checks, c => c.Name == "requirement_coverage" && c.Status == CheckStatus.Warning && !c.Blocking && c.Detail.Contains("FR-2"));
        Assert.Contains(verdict.Checks, c => c.Name == "new_tests" && c.Status == CheckStatus.Warning && !c.Blocking);

        // A CONFIRMED requirement without any change blocks since M14 (a live job was "verified" without the
        // requested web screen).
        var confirmed = DeveloperScripts.Task with
        {
            Analysis = DeveloperScripts.Task.Analysis with
            {
                FunctionalRequirements = [.. DeveloperScripts.Task.Analysis.FunctionalRequirements
                    .Select(r => r with { Status = Factory.Core.Analysis.RequirementStatus.Confirmed })],
            },
        };
        var partial = Workspace(("src/SampleShop.Domain/Products/Product.cs", "namespace SampleShop.Domain.Products;"));
        var uncovered = ChangeChecks.Evaluate(confirmed, partial, Report(partial, DeveloperScripts.TestReportOutput("SampleShop")), null);
        Assert.Equal("check_requirement_coverage", uncovered.FailureCode);
        Assert.Contains("FR-2", uncovered.Findings);
        Assert.True(ChangeChecks.Evaluate(DeveloperScripts.Task, partial, Report(partial, DeveloperScripts.TestReportOutput("SampleShop")), null).Verified);

        var clean = Workspace(["FR-1", "FR-2"], ("src/SampleShop.Domain/Products/Product.cs", "namespace SampleShop.Domain.Products;"));
        var passed = ChangeChecks.Evaluate(DeveloperScripts.Task, clean, Report(clean, DeveloperScripts.TestReportOutput("SampleShop")), null);
        Assert.True(passed.Verified);
        Assert.Contains(passed.Checks, c => c.Name == "protected_files" && c.Status == CheckStatus.Passed);
        Assert.Null(passed.FailureCode);
    }
}
