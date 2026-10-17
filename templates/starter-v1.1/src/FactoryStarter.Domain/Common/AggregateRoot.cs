namespace FactoryStarter.Domain.Common;

// Consistency boundary. Changes go through the root's methods, which enforce invariants and
// record domain events for the application layer to dispatch after a successful save.
public abstract class AggregateRoot<TId> : Entity<TId> where TId : notnull
{
    private readonly List<IDomainEvent> _events = [];

    protected AggregateRoot(TId id) : base(id) { }

    public IReadOnlyList<IDomainEvent> DomainEvents => _events;

    protected void Raise(IDomainEvent domainEvent) => _events.Add(domainEvent);

    public void ClearDomainEvents() => _events.Clear();
}
