using System.Text.Json;
using FactoryStarter.Api;
using FactoryStarter.Domain.Common;

namespace FactoryStarter.Tests;

public class ErrorResponseTests
{
    [Fact]
    public void ViolatedBusinessRulesAreClientErrorsWithTheirCode()
    {
        var (status, body) = ErrorResponses.For(new DomainException("name_required"));
        Assert.Equal(400, status);
        Assert.Equal("{\"error\":\"name_required\"}", JsonSerializer.Serialize(body));
    }

    [Fact]
    public void OtherErrorsRevealNoDetails()
    {
        var (status, body) = ErrorResponses.For(new InvalidOperationException("connection string with secrets"));
        Assert.Equal(500, status);
        Assert.DoesNotContain("secrets", JsonSerializer.Serialize(body));
    }
}
