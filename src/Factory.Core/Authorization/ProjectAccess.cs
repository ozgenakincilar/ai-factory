namespace Factory.Core.Authorization;

public enum PrincipalKind { Human, Service }
public enum ProjectRole { Reader, Generator, Reviewer }

// Constructed only at the authenticated application boundary; never part of agent input.
public sealed record TrustedIdentity(string Issuer, string Subject, PrincipalKind Kind);

public interface IProjectAccessStore
{
    Task<bool> HasAccessAsync(TrustedIdentity identity, string projectId, ProjectRole role,
        CancellationToken cancellationToken = default);
}
