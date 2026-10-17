namespace FactoryStarter.Domain.Common;

// A business rule was violated. The code is stable and safe to show to clients.
public sealed class DomainException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
