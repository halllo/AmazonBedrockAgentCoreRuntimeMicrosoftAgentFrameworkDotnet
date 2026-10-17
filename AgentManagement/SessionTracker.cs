using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

public sealed record SessionInfo(
    string SessionId, string RuntimeVersion, DateTimeOffset StartedAt, DateTimeOffset LastActivityAt, DateTimeOffset ExpiresAt);

/// <summary>
/// Tracks the sessions this instance has invoked, since AgentCore has no API to list runtime sessions.
/// In memory only: sessions started elsewhere or before a restart are not known. Expiry mirrors the
/// runtime's lifecycle settings (idle timeout, max lifetime), so it is an estimate of AgentCore's state.
/// </summary>
public sealed class SessionTracker(IOptionsMonitor<AgentRuntimeOptions> options, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();

    public void Touch(string sessionId, string runtimeVersion)
    {
        var now = time.GetUtcNow();
        _sessions.AddOrUpdate(
            sessionId,
            _ => new SessionInfo(sessionId, runtimeVersion, now, now, ExpiresAt(now, now)),
            (_, s) => IsExpired(s, now)
                // AgentCore starts a fresh session (on the current runtime version) for an expired id.
                ? new SessionInfo(sessionId, runtimeVersion, now, now, ExpiresAt(now, now))
                : s with { LastActivityAt = now, ExpiresAt = ExpiresAt(s.StartedAt, now) });
    }

    public void Remove(string sessionId) => _sessions.TryRemove(sessionId, out _);

    public IReadOnlyList<SessionInfo> GetActive()
    {
        var now = time.GetUtcNow();
        foreach (var expired in _sessions.Values.Where(s => IsExpired(s, now)))
        {
            _sessions.TryRemove(KeyValuePair.Create(expired.SessionId, expired));
        }

        return [.. _sessions.Values.OrderBy(s => s.StartedAt)];
    }

    private bool IsExpired(SessionInfo s, DateTimeOffset now) => now >= s.ExpiresAt;

    private DateTimeOffset ExpiresAt(DateTimeOffset startedAt, DateTimeOffset lastActivityAt)
    {
        var o = options.CurrentValue;
        var idle = lastActivityAt + o.IdleSessionTimeout;
        var max = startedAt + o.MaxSessionLifetime;
        return idle < max ? idle : max;
    }
}
