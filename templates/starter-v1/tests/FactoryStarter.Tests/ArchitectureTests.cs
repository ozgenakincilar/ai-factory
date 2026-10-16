using System.Reflection;
using FactoryStarter.Domain.Common;

namespace FactoryStarter.Tests;

// Enforces the mandatory DDD dependency direction: Domain <- Application <- Infrastructure <- Api.
public class ArchitectureTests
{
    private const string Root = "FactoryStarter";
    private static readonly Assembly Domain = typeof(IDomainEvent).Assembly;
    private static readonly Assembly Application = typeof(FactoryStarter.Application.DependencyInjection).Assembly;
    private static readonly Assembly Infrastructure = typeof(FactoryStarter.Infrastructure.DependencyInjection).Assembly;

    private static string[] References(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    [Fact]
    public void Domain_DependsOnlyOnTheBaseClassLibrary()
    {
        var foreign = References(Domain).Where(n => n != "netstandard" && n != "mscorlib" && !n.StartsWith("System", StringComparison.Ordinal));
        Assert.Empty(foreign);
    }

    [Fact]
    public void Application_DependsOnlyOnDomain()
    {
        var references = References(Application);
        Assert.DoesNotContain(references, n => n.StartsWith(Root, StringComparison.Ordinal) && n != Root + ".Domain");
        Assert.DoesNotContain(references, n => n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
            n.StartsWith("Npgsql", StringComparison.Ordinal) || n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    [Fact]
    public void Infrastructure_DoesNotDependOnTheApi()
    {
        Assert.DoesNotContain(References(Infrastructure), n => n == Root + ".Api");
    }
}
