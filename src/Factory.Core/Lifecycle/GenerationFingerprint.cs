using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Factory.Core.Analysis;
using Factory.Core.Workflows;

namespace Factory.Core.Lifecycle;

// Version 1: ASCII magic, signed int32 big-endian lengths/counts (-1=null), strict UTF-8.
// No normalization; array order and null/empty differences are intentional.
public static class GenerationFingerprint
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static string Compute(GenerationCommand command)
    {
        Validate(command);
        using var bytes = new MemoryStream();
        bytes.Write("UAF-M6-COMMAND"u8); Integer(bytes, 1); Integer(bytes, 1);
        Text(bytes, command.WorkItemId is null ? "Create" : "Generate");
        Text(bytes, command.WorkflowVersion); Text(bytes, command.Request.ProjectId);
        Text(bytes, command.WorkItemId?.ToString("N")); Text(bytes, command.ExpectedETag);
        Input(bytes, command.Request);
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }

    public static string InputHash(WorkflowRequest request)
    {
        using var bytes = new MemoryStream(); bytes.Write("UAF-M6-INPUT"u8); Integer(bytes, 1);
        Text(bytes, request.ProjectId); Input(bytes, request);
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }

    public static void Validate(GenerationCommand command)
    {
        var request = command.Request;
        if (command.Key == Guid.Empty || request is null || command.WorkflowVersion != LifecycleLimits.WorkflowVersion ||
            string.IsNullOrWhiteSpace(request.ProjectId) || request.ProjectId.Length > 200 ||
            string.IsNullOrWhiteSpace(request.Task) || request.Context is null ||
            !AnalysisValidator.HasValidSources(request.ConfirmedRequirements) ||
            (command.WorkItemId is null ? command.ExpectedETag is not null :
                command.WorkItemId == Guid.Empty || !WorkItemETag.IsValid(command.ExpectedETag, command.WorkItemId.Value)))
            throw new LifecycleException(LifecycleError.InvalidCommand);
        try
        {
            using var bytes = new MemoryStream(); Text(bytes, request.ProjectId); Input(bytes, request);
            if (bytes.Length > LifecycleLimits.RequestBytes) throw new LifecycleException(LifecycleError.InvalidCommand);
        }
        catch (EncoderFallbackException) { throw new LifecycleException(LifecycleError.InvalidCommand); }
    }

    private static void Input(Stream bytes, WorkflowRequest request)
    {
        Text(bytes, request.Task); Text(bytes, request.Context);
        Integer(bytes, request.ConfirmedRequirements?.Count ?? -1);
        if (request.ConfirmedRequirements is { } sources)
            foreach (var source in sources) { Text(bytes, source.Id); Text(bytes, source.Description); }
    }
    private static void Text(Stream bytes, string? value)
    {
        if (value is null) { Integer(bytes, -1); return; }
        if (value.Contains('\0')) throw new LifecycleException(LifecycleError.InvalidCommand);
        var encoded = Utf8.GetBytes(value); Integer(bytes, encoded.Length); bytes.Write(encoded);
    }
    private static void Integer(Stream bytes, int value)
    {
        Span<byte> encoded = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(encoded, value); bytes.Write(encoded);
    }
}
