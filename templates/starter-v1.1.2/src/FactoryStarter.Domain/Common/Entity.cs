namespace FactoryStarter.Domain.Common;

// Identity-based equality: two entities are the same when their ids are equal.
public abstract class Entity<TId> where TId : notnull
{
    protected Entity(TId id) => Id = id;

    public TId Id { get; }

    public override bool Equals(object? obj) =>
        obj is Entity<TId> other && other.GetType() == GetType() && EqualityComparer<TId>.Default.Equals(Id, other.Id);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}
