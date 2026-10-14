namespace Factory.Core.Reviews;

// Version 1: explicit code points, independent of culture and future Unicode category changes.
public static class DecisionReasonPolicy
{
    public const string Whitespace = "\u0009\u000A\u000B\u000C\u000D\u0020\u0085\u00A0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200A\u2028\u2029\u202F\u205F\u3000";

    public static bool IsValid(ReviewDecision decision, string? reason)
    {
        if (reason is null) return decision == ReviewDecision.Approve;
        if (reason.Length > 4000) return false;
        var hasExplanation = false;
        for (var i = 0; i < reason.Length; i++)
        {
            var c = reason[i];
            if (c == '\0' || char.IsLowSurrogate(c)) return false;
            if (char.IsHighSurrogate(c))
            {
                if (++i == reason.Length || !char.IsLowSurrogate(reason[i])) return false;
            }
            if (!Whitespace.Contains(c)) hasExplanation = true;
        }
        return decision == ReviewDecision.Approve || hasExplanation;
    }
}
