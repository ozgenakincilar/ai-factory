using FactoryStarter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FactoryStarter.Tests;

// Builds and validates the EF Core model without a database, so a mapping error (for example an
// aggregate without a constructor EF Core can use) fails here with EF Core's own message instead of
// surfacing later as an HTTP 500.
public class PersistenceTests
{
    [Fact]
    public void TheEntityModelIsValid()
    {
        using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model-check").Options);
        // EF Core's message only, without its long stack trace.
        try { Assert.NotNull(context.Model); }
        catch (InvalidOperationException ex) { Assert.Fail("EF Core model is invalid: " + ex.Message); }
    }
}
