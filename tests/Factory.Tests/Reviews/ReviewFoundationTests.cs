using System.Text.Json;
using Factory.Core.Architecture;
using Factory.Core.Reviews;
using Factory.Core.Workflows;
using Factory.Infrastructure.Reviews;
using Factory.Tests.Architecture;

namespace Factory.Tests.Reviews;

internal static class ReviewSamples
{
    internal static WorkflowRequest Request => new("review-test", "Analyze tasks", "No selected stack", ArchitectSamples.Request.ConfirmedRequirements);
    internal static WorkflowResult Result => new(Guid.NewGuid(), WorkflowStatus.AwaitingHumanReview,
        WorkflowStageStatus.Completed, WorkflowStageStatus.Completed, ArchitectSamples.Request.Analysis, ArchitectSamples.Proposal);
}

public class ReviewFoundationTests
{
    [Theory]
    [InlineData(ReviewDecision.Approve, ReviewState.Approved)]
    [InlineData(ReviewDecision.Reject, ReviewState.Rejected)]
    [InlineData(ReviewDecision.RequestChanges, ReviewState.ChangesRequested)]
    public void DecisionRules_AreTerminalAndRevisionRequiresNewReview(ReviewDecision decision, ReviewState expected)
    {
        var state = ReviewStateMachine.Decide(ReviewState.PendingReview, decision, "Human feedback");
        Assert.Equal(expected, state);
        Assert.Throws<InvalidOperationException>(() => ReviewStateMachine.Decide(state, ReviewDecision.Approve, null));
        Assert.Equal(ReviewState.PendingReview, ReviewStateMachine.PublishRevision(state));
    }

    [Theory]
    [InlineData(ReviewDecision.Reject)]
    [InlineData(ReviewDecision.RequestChanges)]
    [InlineData((ReviewDecision)99)]
    public void InvalidDecisionOrMissingReason_IsRejected(ReviewDecision decision) =>
        Assert.Throws<ArgumentException>(() => ReviewStateMachine.Decide(ReviewState.PendingReview, decision, null));

    [Fact]
    public void Serialization_DetachesEvidenceAndBytesAndPreservesProvenance()
    {
        var sources = ReviewSamples.Request.ConfirmedRequirements!.ToList();
        var request = ReviewSamples.Request with { ConfirmedRequirements = sources };
        var artifact = ReviewArtifactSerializer.Create(Guid.NewGuid(), 1, request, ReviewSamples.Result, DateTimeOffset.UtcNow);
        sources.Clear();
        var copy = artifact.GetContent();
        copy[0] = 0;
        var envelope = ReviewArtifactSerializer.Read(artifact);
        Assert.Single(envelope.Request.ConfirmedRequirements!);
        Assert.Equal(ProposalStatus.Proposed, envelope.Architecture.Status);
        Assert.Equal(Factory.Core.Analysis.RequirementStatus.Suggested, envelope.Analysis.FunctionalRequirements[1].Status);
        Assert.Equal(artifact.ContentHash, ReviewArtifactSerializer.Hash(artifact.GetContent()));
        Assert.True(ArchitectureValidator.IsValid(envelope.Architecture, new(envelope.Request.ProjectId,
            envelope.Request.Task, envelope.Request.Context, envelope.Request.ConfirmedRequirements, envelope.Analysis)));
    }

    [Fact]
    public void StableSerializationAndVersionBinding()
    {
        var id = Guid.NewGuid();
        var result = ReviewSamples.Result;
        var time = DateTimeOffset.UtcNow;
        var first = ReviewArtifactSerializer.Create(id, 1, ReviewSamples.Request, result, time);
        var same = ReviewArtifactSerializer.Create(id, 1, ReviewSamples.Request, result, time);
        var revised = ReviewArtifactSerializer.Create(id, 2, ReviewSamples.Request, result, time);
        Assert.Equal(first.GetContent(), same.GetContent());
        Assert.NotEqual(first.ContentHash, revised.ContentHash);
        Assert.Throws<ReviewIntegrityException>(() => ReviewArtifactSerializer.Read(new(id, 2, first.GetContent(), first.ContentHash)));
    }

    [Fact]
    public void TamperedContentOrHash_IsRejected()
    {
        var a = ReviewArtifactSerializer.Create(Guid.NewGuid(), 1, ReviewSamples.Request, ReviewSamples.Result, DateTimeOffset.UtcNow);
        var bytes = a.GetContent();
        bytes[0] ^= 1;
        Assert.Throws<ReviewIntegrityException>(() => ReviewArtifactSerializer.Read(new(a.CaseId, 1, bytes, a.ContentHash)));
        Assert.Throws<ReviewIntegrityException>(() => ReviewArtifactSerializer.Read(new(a.CaseId, 1, a.GetContent(), new string('0', 64))));
    }

    [Fact]
    public void FailedOrInvalidWorkflow_CannotBecomeArtifact()
    {
        Assert.Throws<ArgumentException>(() => ReviewArtifactSerializer.Create(Guid.NewGuid(), 1, ReviewSamples.Request,
            ReviewSamples.Result with { Status = WorkflowStatus.Failed }, DateTimeOffset.UtcNow));
        Assert.Throws<ReviewIntegrityException>(() => ReviewArtifactSerializer.Create(Guid.NewGuid(), 1, ReviewSamples.Request,
            ReviewSamples.Result with { Architecture = ArchitectSamples.Proposal with { ConfirmedConstraintRequirementIds = ["FR-2"] } }, DateTimeOffset.UtcNow));
    }
}
