using System.Text.Json;
using System.Text.Json.Serialization;
using Factory.Core.Orchestration;
using Npgsql;

namespace Factory.Infrastructure.Orchestration;

// Receives commit-time run notifications (orchestration_runs). Notifications sent while the
// connection is down are lost; consumers must treat them as hints and re-read state.
public sealed class PostgresRunEventListener(string connection)
{
    internal const string Channel = "orchestration_runs";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public bool Configured => !string.IsNullOrWhiteSpace(connection);

    // Returns only when cancelled; connection failures propagate so the caller can back off and reconnect.
    public async Task ListenAsync(Func<RunChanged, Task> onChange, Action? listening, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(ct);
        var pending = new List<RunChanged>();
        db.Notification += (_, e) => { if (e.Channel == Channel && Parse(e.Payload) is { } change) pending.Add(change); };
        await using (var listen = new NpgsqlCommand($"LISTEN {Channel}", db)) await listen.ExecuteNonQueryAsync(ct);
        listening?.Invoke();
        while (true)
        {
            await db.WaitAsync(ct);
            var batch = pending.ToArray(); pending.Clear();
            foreach (var change in batch) await onChange(change);
        }
    }

    internal static RunChanged? Parse(string payload)
    {
        try
        {
            var change = JsonSerializer.Deserialize<RunChanged>(payload, Json);
            return change is { RunId: var id, ProjectId: { Length: > 0 and <= 200 } } && id != Guid.Empty && Enum.IsDefined(change.Status) ? change : null;
        }
        catch (JsonException) { return null; }
    }
}
