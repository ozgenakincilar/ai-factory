using FactoryStarter.Domain.Common;

namespace FactoryStarter.Api;

// Unhandled exceptions become responses: a violated business rule is the client's error and carries its
// stable code; anything else is a 500 without details.
public static class ErrorResponses
{
    public static (int Status, object Body) For(Exception? error) => error is DomainException domain
        ? (StatusCodes.Status400BadRequest, new { error = domain.Code })
        : (StatusCodes.Status500InternalServerError, new { error = "internal_error" });
}
