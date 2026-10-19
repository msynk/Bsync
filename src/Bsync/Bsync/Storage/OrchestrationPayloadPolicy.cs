namespace Bsync.Storage;

/// <summary>
/// Explicit registration for protected payload namespaces and exact clear control keys. Unknown keys fail closed.
/// Create one stable policy per account store; every writer must use the same registrations across restarts.
/// </summary>
public sealed class OrchestrationPayloadPolicy
{
    private readonly Dictionary<string, string> _protected = new(StringComparer.Ordinal);
    private readonly HashSet<string> _controls = new(StringComparer.Ordinal);
    private bool _frozen;

    /// <summary>Registers an application-data namespace ending in '/'. Overlapping protected namespaces are refused.</summary>
    public OrchestrationPayloadPolicy Protect(string prefix, string kind)
    {
        if (_frozen) throw new InvalidOperationException("Payload policy is already in use.");
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix); ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        OrchestrationValidation.Key(prefix);
        if (!prefix.EndsWith('/') || kind.Length > 128 || kind.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw new ArgumentException("Expected a bounded namespace and portable record kind.");
        if (_protected.Keys.Any(p => p.StartsWith(prefix, StringComparison.Ordinal) || prefix.StartsWith(p, StringComparison.Ordinal)))
            throw new ArgumentException("Protected namespaces must not overlap.", nameof(prefix));
        _protected.Add(prefix, kind); return this;
    }

    /// <summary>Registers an exact opaque coordination key, including an execution-lease key. Never store application payloads here.</summary>
    public OrchestrationPayloadPolicy Control(string key)
    {
        if (_frozen) throw new InvalidOperationException("Payload policy is already in use.");
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        OrchestrationValidation.Key(key);
        if (!_controls.Add(key)) throw new ArgumentException("Control key already registered.", nameof(key));
        return this;
    }

    /// <summary>Freezes registrations and returns the selector for ProtectedOrchestrationStore. Exact control keys take precedence.</summary>
    public Func<string, string?> CreateSelector()
    {
        _frozen = true;
        return key =>
        {
            if (_controls.Contains(key)) return null;
            foreach (var item in _protected) if (key.StartsWith(item.Key, StringComparison.Ordinal)) return item.Value;
            throw new InvalidOperationException("Orchestration record kind is not registered in the payload policy.");
        };
    }
}
