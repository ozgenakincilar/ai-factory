namespace Factory.Core.Agents;

public interface IAgent
{
    string Name { get; }

    Task<AgentResult> ExecuteAsync(
        AgentRequest request,
        CancellationToken cancellationToken = default);
}