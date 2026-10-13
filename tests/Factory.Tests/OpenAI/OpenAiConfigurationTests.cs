using Factory.Infrastructure.OpenAI;

namespace Factory.Tests.OpenAI;

public class OpenAiConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ValidateApiKey_MissingKey_ThrowsSafeConfigurationError(string? key)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => OpenAiConfiguration.ValidateApiKey(key));

        Assert.Contains("OpenAI:ApiKey is not configured", error.Message);
    }

    [Theory]
    [InlineData("test-secret\u0000")]
    [InlineData("test\u007F-secret")]
    [InlineData("test-secret\r\n")]
    [InlineData(" test-secret")]
    [InlineData("test-secret\u200B")]
    [InlineData("Bearer test-secret")]
    [InlineData("\"test-secret\"")]
    [InlineData("'test-secret'")]
    public void ValidateApiKey_MalformedKey_RejectsWithoutExposingCredential(string key)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => OpenAiConfiguration.ValidateApiKey(key));

        Assert.Contains("Re-enter the original API key", error.Message);
        Assert.DoesNotContain("test-secret", error.Message);
        Assert.DoesNotContain(key, error.Message);
    }

    [Fact]
    public void ValidateApiKey_PrintableCredential_DoesNotRequireSpecificPrefixOrLength()
    {
        OpenAiConfiguration.ValidateApiKey("synthetic-key_123-ABC");
    }
}
