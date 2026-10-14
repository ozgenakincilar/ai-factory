using Factory.Core.Reviews;
using Factory.Infrastructure.Reviews;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Factory.Tests.Reviews;

public static class DecisionReasonCases
{
    public static IEnumerable<object?[]> All()
    {
        // Independent specification, not derived from the implementation's whitespace constant.
        int[] whitespace = [9,10,11,12,13,32,133,160,5760,8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,8232,8233,8239,8287,12288];
        var examples = whitespace.Select(c => ($"U+{c:X4}", (string?)char.ConvertFromUtf32(c), false)).ToList();
        examples.AddRange([
            ("null", null, false), ("empty", "", false), ("mixed-whitespace", " \t\r\n\u00A0\u2007\u3000", false),
            ("plain", "Please explain the missing requirement.", true),
            ("multiline-unicode", "\t\u00A0Fix\r\n\u00D6rnek \u4FEE\u6B63 \U0001F642\u3000", true),
            ("zero-width-space-is-not-policy-whitespace", "\u200B", true), ("BOM-is-not-policy-whitespace", "\uFEFF", true),
            ("NUL", "bad\0reason", false), ("limit", new string('x',4000), true), ("over-limit", new string('x',4001), false),
            ("supplementary-limit", string.Concat(Enumerable.Repeat("\U0001F642",2000)), true),
            ("supplementary-over-limit", string.Concat(Enumerable.Repeat("\U0001F642",2001)), false)]);
        foreach (var (name, reason, explanation) in examples)
        foreach (var action in Enum.GetValues<ReviewDecision>())
        {
            var validText = reason is null || (reason.Length <= 4000 && !reason.Contains('\0'));
            yield return [name, action, reason, validText && (action == ReviewDecision.Approve || explanation)];
        }
    }
    public static IEnumerable<object?[]> Http() => All().Where(x => x[2] is not string s || s.Length < 100);
}

public class DecisionReasonTests
{
    [Theory]
    [MemberData(nameof(DecisionReasonCases.All), MemberType = typeof(DecisionReasonCases))]
    public void DomainAndCommandValidationAgree(string label, ReviewDecision action, string? reason, bool valid)
    {
        Assert.True(DecisionReasonPolicy.IsValid(action,reason) == valid,label);
        var command = new DecisionCommand(1,1,new string('a',64),action,reason,Guid.NewGuid());
        if (valid)
        {
            DecisionValidation.Validate("project",Guid.NewGuid(),command);
            Assert.NotEqual(ReviewState.PendingReview,ReviewStateMachine.Decide(ReviewState.PendingReview,action,reason));
        }
        else
        {
            Assert.Throws<ReviewCommandException>(() => DecisionValidation.Validate("project",Guid.NewGuid(),command));
            Assert.Throws<ArgumentException>(() => ReviewStateMachine.Decide(ReviewState.PendingReview,action,reason));
        }
    }

    [Theory]
    [InlineData(0xD800, false)] [InlineData(0xDC00, false)] [InlineData(0xD800, true)]
    public void MalformedUtf16CannotEnterDomain(int codePoint, bool padded)
    {
        // Construct here: attribute/test-discovery serialization can replace unpaired surrogates.
        var reason = new string((char)codePoint, 1);
        if (padded) reason = "x" + reason + "x";
        Assert.False(DecisionReasonPolicy.IsValid(ReviewDecision.Approve, reason));
        Assert.Throws<ArgumentException>(() => ReviewStateMachine.Decide(ReviewState.PendingReview,ReviewDecision.Reject,reason));
    }

    [DecisionPostgresTheory]
    [MemberData(nameof(DecisionReasonCases.All), MemberType = typeof(DecisionReasonCases))]
    public async Task RestrictedRawSql_RejectsInvalidReasonsAndRollsBackCaseClaim(string label, ReviewDecision action, string? reason, bool valid)
    {
        Assert.True(DecisionReasonPolicy.IsValid(action,reason) == valid,label);
        var seed = await DecisionTestDatabase.Seed();
        await using var db = new ReviewDbContext(DecisionTestDatabase.Options());
        await using var tx = await db.Database.BeginTransactionAsync();
        if (valid)
        {
            await Insert(db, seed, action, reason);
            await tx.CommitAsync();
            await DecisionTestDatabase.AssertCounts(seed,2,1,1);
            var stored = await DecisionTestDatabase.Store().GetAsync(seed.Identity,seed.Case.ProjectId,seed.Case.Id);
            Assert.Equal(reason,stored.Decision!.Reason); // No trimming or normalization.
        }
        else
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Insert(db,seed,action,reason));
            Assert.Contains(error.SqlState,new[] { "23514", "22001", "22021" });
            // PostgreSQL turns COMMIT of an aborted transaction into ROLLBACK. No decision may persist.
            await tx.CommitAsync();
            await DecisionTestDatabase.AssertCounts(seed,1,0,0);
        }
    }

    internal static async Task Insert(ReviewDbContext db, DecisionSeed seed, ReviewDecision action, string? reason)
    {
        var state = action switch { ReviewDecision.Approve => "Approved", ReviewDecision.Reject => "Rejected", _ => "ChangesRequested" };
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE review."ReviewCases" SET "State"={state},"Revision"=2 WHERE "Id"={seed.Case.Id}""");
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO review."DecisionEvents" ("Id","ProjectId","CaseId","Version","ContentHash","PrincipalId","Issuer","Subject",
                "ExpectedRevision","ResultRevision","Decision","Reason","ResultState","CommandVersion","Key")
            VALUES ({id},{seed.Case.ProjectId},{seed.Case.Id},1,{seed.Artifact.ContentHash},{seed.Principal.Id},{seed.Principal.Issuer},{seed.Principal.Subject},
                1,2,{action.ToString()},{reason},{state},1,{Guid.NewGuid()})
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO review."CommandReceipts" ("ProjectId","PrincipalId","CommandVersion","Key","DecisionId","Fingerprint","ResponseVersion")
            SELECT "ProjectId","PrincipalId",1,"Key","Id","Fingerprint",1 FROM review."DecisionEvents" WHERE "Id"={id}
            """);
    }
}
