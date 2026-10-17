namespace FactoryStarter.Domain.Common;

public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}
