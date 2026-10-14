namespace Factory.Core.Reviews;

public enum ReviewState { PendingReview, Approved, Rejected, ChangesRequested }
public enum ReviewDecision { Approve, Reject, RequestChanges }

// Pure domain rules only. This is not an authenticated approval service.
public static class ReviewStateMachine
{
    public static ReviewState Decide(ReviewState state, ReviewDecision decision, string? reason)
    {
        if (state != ReviewState.PendingReview)
            throw new InvalidOperationException("review_already_decided");
        if (!Enum.IsDefined(decision)) throw new ArgumentException("invalid_review_decision");
        if (!DecisionReasonPolicy.IsValid(decision, reason))
            throw new ArgumentException("review_reason_required");
        return decision switch
        {
            ReviewDecision.Approve => ReviewState.Approved,
            ReviewDecision.Reject => ReviewState.Rejected,
            _ => ReviewState.ChangesRequested
        };
    }

    public static ReviewState PublishRevision(ReviewState state) => Enum.IsDefined(state)
        ? ReviewState.PendingReview : throw new ArgumentException("invalid_review_state");
}
