using Amazon.BedrockAgentCoreControl;
using Amazon.BedrockAgentCoreControl.Model;
using Microsoft.Extensions.Options;

/// <summary>Desired state of the AgentCore Runtime, bound from the "AgentRuntime" config section.</summary>
public sealed class AgentRuntimeOptions
{
    /// <summary>Runtime name; must match <c>[a-zA-Z][a-zA-Z0-9_]{0,47}</c>.</summary>
    public string Name { get; set; } = "AgentRuntimeMAF";

    public string? Description { get; set; }

    /// <summary>ECR image URI of the AgentRuntime container (linux/arm64).</summary>
    public string ContainerUri { get; set; } = "";

    /// <summary>Execution role the runtime assumes (needs bedrock:InvokeModel*, ECR pull, logs).</summary>
    public string RoleArn { get; set; } = "";

    /// <summary>PUBLIC or VPC (VPC additionally needs NetworkModeConfig, not supported here).</summary>
    public string NetworkMode { get; set; } = "PUBLIC";

    public Dictionary<string, string> EnvironmentVariables { get; set; } = [];

    public TimeSpan ReadyTimeout { get; set; } = TimeSpan.FromMinutes(10);
}

public sealed record RegisteredAgentRuntime(string Id, string Arn, string Version);

/// <summary>
/// Registers the AgentCore Runtime on first use: creates it when no runtime with the configured name
/// exists, updates it when the configuration differs, and otherwise reuses it as is.
/// </summary>
public sealed class AgentRuntimeRegistry(
    IAmazonBedrockAgentCoreControl control,
    IOptions<AgentRuntimeOptions> options,
    ILogger<AgentRuntimeRegistry> logger)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private RegisteredAgentRuntime? _runtime;

    public async Task<RegisteredAgentRuntime> GetOrRegisterAsync(CancellationToken ct)
    {
        if (_runtime is { } cached)
        {
            return cached;
        }

        await _lock.WaitAsync(ct);
        try
        {
            return _runtime ??= await RegisterAsync(options.Value, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<RegisteredAgentRuntime> RegisterAsync(AgentRuntimeOptions desired, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(desired.ContainerUri) || string.IsNullOrWhiteSpace(desired.RoleArn))
        {
            throw new InvalidOperationException("AgentRuntime:ContainerUri and AgentRuntime:RoleArn must be configured.");
        }

        var existing = await FindByNameAsync(desired.Name, ct);
        if (existing is null)
        {
            try
            {
                logger.LogInformation("Creating agent runtime {Name} from {ContainerUri}", desired.Name, desired.ContainerUri);
                var created = await control.CreateAgentRuntimeAsync(new CreateAgentRuntimeRequest
                {
                    AgentRuntimeName = desired.Name,
                    Description = desired.Description,
                    AgentRuntimeArtifact = Artifact(desired),
                    RoleArn = desired.RoleArn,
                    NetworkConfiguration = Network(desired),
                    ProtocolConfiguration = Protocol(),
                    EnvironmentVariables = desired.EnvironmentVariables,
                    ClientToken = Guid.NewGuid().ToString(),
                }, ct);
                return await WaitUntilReadyAsync(created.AgentRuntimeId, desired.ReadyTimeout, ct);
            }
            catch (ConflictException)
            {
                // Another instance created it concurrently; fall through to the reuse/update path.
                existing = await FindByNameAsync(desired.Name, ct)
                    ?? throw new InvalidOperationException($"Agent runtime '{desired.Name}' conflicts but cannot be found.");
            }
        }

        var current = await WaitUntilSettledAsync(existing.AgentRuntimeId, desired.ReadyTimeout, ct);
        if (!HasChanged(current, desired))
        {
            logger.LogInformation("Reusing agent runtime {Name} ({Arn}, version {Version})",
                desired.Name, current.AgentRuntimeArn, current.AgentRuntimeVersion);
            return new RegisteredAgentRuntime(current.AgentRuntimeId, current.AgentRuntimeArn, current.AgentRuntimeVersion);
        }

        logger.LogInformation("Updating agent runtime {Name}: configuration changed", desired.Name);
        await control.UpdateAgentRuntimeAsync(new UpdateAgentRuntimeRequest
        {
            AgentRuntimeId = current.AgentRuntimeId,
            Description = desired.Description,
            AgentRuntimeArtifact = Artifact(desired),
            RoleArn = desired.RoleArn,
            NetworkConfiguration = Network(desired),
            ProtocolConfiguration = Protocol(),
            EnvironmentVariables = desired.EnvironmentVariables,
            ClientToken = Guid.NewGuid().ToString(),
        }, ct);
        return await WaitUntilReadyAsync(current.AgentRuntimeId, desired.ReadyTimeout, ct);
    }

    private async Task<AgentRuntime?> FindByNameAsync(string name, CancellationToken ct)
    {
        string? nextToken = null;
        do
        {
            var page = await control.ListAgentRuntimesAsync(new ListAgentRuntimesRequest { NextToken = nextToken }, ct);
            var match = page.AgentRuntimes?.FirstOrDefault(r => r.AgentRuntimeName == name);
            if (match is not null)
            {
                return match;
            }

            nextToken = page.NextToken;
        }
        while (!string.IsNullOrEmpty(nextToken));

        return null;
    }

    private static bool HasChanged(GetAgentRuntimeResponse current, AgentRuntimeOptions desired)
    {
        var currentEnv = current.EnvironmentVariables ?? [];
        return current.AgentRuntimeArtifact?.ContainerConfiguration?.ContainerUri != desired.ContainerUri
            || current.RoleArn != desired.RoleArn
            || (current.Description ?? "") != (desired.Description ?? "")
            || current.NetworkConfiguration?.NetworkMode?.Value != desired.NetworkMode
            || current.ProtocolConfiguration?.ServerProtocol != ServerProtocol.HTTP
            || currentEnv.Count != desired.EnvironmentVariables.Count
            || desired.EnvironmentVariables.Any(kv => !currentEnv.TryGetValue(kv.Key, out var v) || v != kv.Value);
    }

    /// <summary>Waits for a pending create/update to finish, then returns the runtime in whatever state it settled.</summary>
    private async Task<GetAgentRuntimeResponse> WaitUntilSettledAsync(string id, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        while (true)
        {
            var runtime = await control.GetAgentRuntimeAsync(new GetAgentRuntimeRequest { AgentRuntimeId = id }, timeoutCts.Token);
            if (runtime.Status != AgentRuntimeStatus.CREATING && runtime.Status != AgentRuntimeStatus.UPDATING)
            {
                return runtime;
            }

            logger.LogInformation("Agent runtime {Id} is {Status}, waiting...", id, runtime.Status);
            await Task.Delay(PollInterval, timeoutCts.Token);
        }
    }

    private async Task<RegisteredAgentRuntime> WaitUntilReadyAsync(string id, TimeSpan timeout, CancellationToken ct)
    {
        var runtime = await WaitUntilSettledAsync(id, timeout, ct);
        if (runtime.Status != AgentRuntimeStatus.READY)
        {
            throw new InvalidOperationException(
                $"Agent runtime '{runtime.AgentRuntimeName}' is {runtime.Status}: {runtime.FailureReason}");
        }

        logger.LogInformation("Agent runtime {Arn} is READY (version {Version})", runtime.AgentRuntimeArn, runtime.AgentRuntimeVersion);
        return new RegisteredAgentRuntime(runtime.AgentRuntimeId, runtime.AgentRuntimeArn, runtime.AgentRuntimeVersion);
    }

    private static AgentRuntimeArtifact Artifact(AgentRuntimeOptions o) =>
        new() { ContainerConfiguration = new ContainerConfiguration { ContainerUri = o.ContainerUri } };

    private static NetworkConfiguration Network(AgentRuntimeOptions o) =>
        new() { NetworkMode = new NetworkMode(o.NetworkMode) };

    // The AgentRuntime container speaks the AgentCore HTTP protocol (/ping, /invocations incl. AG-UI streaming).
    private static ProtocolConfiguration Protocol() =>
        new() { ServerProtocol = ServerProtocol.HTTP };
}
