using Factory.Core.Analysis;
using Factory.Core.Lifecycle;
using Factory.Core.Reviews;
using Factory.Core.Workflows;

namespace Factory.Tests.Lifecycle;

public class LifecycleDomainTests
{
    // Fixed v1 wire vectors independently calculated with Python struct.pack('>i') and hashlib.
    public static TheoryData<int, string> GoldenVectors => new()
    {
        { 0, "de0eb7c522622633764ec0193a26119ae1ff76c80852cb9333ee9768a9b41d8f" },
        { 1, "f8e06e762bc0394da03b5e70ff038a6255c7be1839409b1cdec3e7fab3aa9ab8" },
        { 2, "7ab05a78d1197709b4f883612d798e5b5158506d6428c37ee55c4054dad35875" },
        { 3, "b6ca2d82833464f8e00a47fcb99cef270ac125f9dd3f44186760d6c177f91beb" }
    };
    internal static GenerationCommand GoldenCommand(int vector) => new(Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "\"wi-v1:11111111111111111111111111111111:2:22222222222222222222222222222222:1\"", LifecycleLimits.WorkflowVersion,
        new("golden", "task", "context", vector switch
        { 0 => null, 1 => [], 2 => [new("a", "one"), new("b", "two")], _ => [new("b", "two"), new("a", "one")] }));
    [Theory]
    [MemberData(nameof(GoldenVectors))]
    public void GenerateFingerprintMatchesGoldenWireVector(int vector, string expected)
        => Assert.Equal(expected, GenerationFingerprint.Compute(GoldenCommand(vector)));

    private static GenerationCommand Command => new(Guid.NewGuid(), null, null, LifecycleLimits.WorkflowVersion,
        new WorkflowRequest("project", "task", "context", [new ConfirmedRequirement("r1", "exact evidence")]));

    [Fact]
    public void CreateFingerprintDoesNotDependOnKeyOrServerIdentity()
    {
        var command = Command;
        Assert.Equal(GenerationFingerprint.Compute(command), GenerationFingerprint.Compute(command with { Key = Guid.NewGuid() }));
        Assert.NotEqual(GenerationFingerprint.Compute(command), GenerationFingerprint.Compute(command with { Request = command.Request with { Task = "task " } }));
    }

    [Fact]
    public void CanonicalInputPreservesUnicodeNewlinesOrderAndNull()
    {
        var request = Command.Request;
        var variants = new[] { request, request with { Task = "\u00e9" }, request with { Task = "e\u0301" },
            request with { Context = "\r\n" }, request with { Context = "\n" },
            request with { ConfirmedRequirements = null }, request with { ConfirmedRequirements = [] },
            request with { ConfirmedRequirements = [new("a", "x"), new("b", "y")] },
            request with { ConfirmedRequirements = [new("b", "y"), new("a", "x")] } };
        Assert.Equal(variants.Length, variants.Select(GenerationFingerprint.InputHash).Distinct().Count());
    }

    [Fact]
    public void InvalidUnicodeCannotEnterCommand()
    {
        var command = Command;
        // Construct in-process: attribute metadata/test discovery can replace unpaired surrogates.
        foreach (var invalid in new[] { "\0", new string((char)0xd800, 1) })
            Assert.Throws<LifecycleException>(() => GenerationFingerprint.Compute(command with { Request = command.Request with { Context = invalid } }));
    }

    [Fact]
    public void AggregatePreservesVersionBindingAndHistoricalApproval()
    {
        var originalHash = new string('a', 64); var nextHash = new string('b', 64);
        var review = ReviewCase.Create(Guid.NewGuid(), "project", originalHash);
        Assert.Throws<ReviewIntegrityException>(() => review.Decide(1, 1, nextHash, ReviewDecision.Approve, null));
        Assert.Throws<ReviewConflictException>(() => review.Publish(2, nextHash));
        review.Decide(1, 1, originalHash, ReviewDecision.Approve, null);
        Assert.Equal(ReviewState.Approved, review.State);
        Assert.Throws<InvalidOperationException>(() => review.Decide(2, 1, originalHash, ReviewDecision.Approve, null));
        review.Publish(2, nextHash);
        Assert.Equal(2, review.Version); Assert.Equal(3, review.Revision);
        Assert.Equal(ReviewState.PendingReview, review.State); Assert.Equal(nextHash, review.ContentHash);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    [InlineData("\u0085\u3000")]
    public void AggregateRejectsBlankReasonsWithoutChangingState(string reason)
    {
        var hash = new string('a', 64); var review = ReviewCase.Create(Guid.NewGuid(), "project", hash);
        Assert.Throws<ArgumentException>(() => review.Decide(1, 1, hash, ReviewDecision.Reject, reason));
        Assert.Equal(1, review.Revision); Assert.Equal(ReviewState.PendingReview, review.State);
    }

    [Fact]
    public void WorkItemTagsAreExactStrongValidators()
    {
        var id = Guid.NewGuid(); var tag = WorkItemETag.Format(id, 1, null, 0);
        Assert.True(WorkItemETag.IsValid(tag, id));
        foreach (var invalid in new[] { "W/" + tag, "*", tag + "," + tag, tag.Replace(":1:", ":01:"), tag.ToUpperInvariant() })
            Assert.False(WorkItemETag.IsValid(invalid, id));
        Assert.False(WorkItemETag.IsValid(tag, Guid.NewGuid()));
        var caseId = Guid.NewGuid();
        Assert.NotEqual(WorkItemETag.Format(id, 1, caseId, 1), WorkItemETag.Format(id, 1, caseId, 2));
    }
}
