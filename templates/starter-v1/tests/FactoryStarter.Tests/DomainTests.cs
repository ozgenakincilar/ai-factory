using FactoryStarter.Domain.Common;

namespace FactoryStarter.Tests;

public class DomainTests
{
    private sealed record Happened(DateTimeOffset OccurredAt) : IDomainEvent;

    private sealed class Sample(Guid id) : AggregateRoot<Guid>(id)
    {
        public void Act() => Raise(new Happened(DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void AggregateRecordsAndClearsDomainEvents()
    {
        var aggregate = new Sample(Guid.NewGuid());
        aggregate.Act();
        Assert.Single(aggregate.DomainEvents);
        aggregate.ClearDomainEvents();
        Assert.Empty(aggregate.DomainEvents);
    }

    [Fact]
    public void EntitiesAreEqualByTypeAndId()
    {
        var id = Guid.NewGuid();
        Assert.Equal(new Sample(id), new Sample(id));
        Assert.NotEqual(new Sample(id), new Sample(Guid.NewGuid()));
    }
}
