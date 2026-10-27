namespace Bsync.Server.SqlServer.Workflows;

/// <summary>Receiver guarantees, supplied by the server integration, never inferred from an HTTP method.</summary>
public sealed record ExternalReceiverCapabilities(bool Deduplicates, bool StatusLookup);

/// <summary>Lookup must describe this exact immutable idempotency key. NotExecuted is a definitive terminal non-execution.</summary>
public enum ExternalEffectStatus
{
    /// <summary>The receiver cannot establish an outcome.</summary>
    Unknown,
    /// <summary>Definitive terminal non-execution.</summary>
    NotExecuted,
    /// <summary>Execution is still pending.</summary>
    Pending,
    /// <summary>Execution succeeded.</summary>
    Succeeded,
    /// <summary>Execution was permanently rejected.</summary>
    Rejected
}

/// <summary>Credentials are acquired by this host adapter at dispatch time; they never enter the outbox payload.</summary>
public interface IExternalEffectReceiver
{
    /// <summary>Stable non-secret receiver identity, including account and operation semantics; never reuse for another receiver.</summary>
    string Identity { get; }
    /// <summary>Declared durable receiver guarantees.</summary>
    ExternalReceiverCapabilities Capabilities { get; }
    /// <summary>Acquires current authorization without storing credentials in the request.</summary>
    Task<bool> AuthorizeAsync(ExternalEffect effect, CancellationToken cancellationToken);
    /// <summary>Looks up this exact immutable effect identity.</summary>
    Task<ExternalEffectStatus> LookupAsync(ExternalEffect effect, CancellationToken cancellationToken);
    /// <summary>Dispatches with the stable idempotency key.</summary>
    Task<ExternalEffectOutcome> SendAsync(ExternalEffect effect, CancellationToken cancellationToken);
}

/// <summary>Receiver policy for an already durably claimed outbox row; it does not own a database transaction.</summary>
public static class ExternalEffectDelivery
{
    /// <summary>Resolves prior ambiguity according to declared receiver capabilities before sending.</summary>
    public static async Task<ExternalEffectOutcome> DispatchAsync(IExternalEffectReceiver receiver, ExternalEffect effect,
        bool previouslyAttempted, CancellationToken cancellationToken)
    {
        if (!await receiver.AuthorizeAsync(effect, cancellationToken)) return ExternalEffectOutcome.CredentialsUnavailable;
        if (previouslyAttempted && receiver.Capabilities.StatusLookup)
        {
            var status = await receiver.LookupAsync(effect, cancellationToken);
            switch (status)
            {
                case ExternalEffectStatus.Succeeded: return ExternalEffectOutcome.Succeeded;
                case ExternalEffectStatus.Rejected: return ExternalEffectOutcome.Rejected;
                case ExternalEffectStatus.Pending: return ExternalEffectOutcome.RetryLater;
                case ExternalEffectStatus.NotExecuted: previouslyAttempted = false; break;
                case ExternalEffectStatus.Unknown: break;
                default: throw new InvalidDataException("Unknown external status.");
            }
        }
        if (previouslyAttempted && !receiver.Capabilities.Deduplicates) return ExternalEffectOutcome.ReconciliationRequired;
        try { return await receiver.SendAsync(effect, cancellationToken); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A send exception says nothing about whether the receiver committed. Only declared reconciliation
            // mechanisms justify another automatic attempt. The durable attempt marker predates this call.
            return receiver.Capabilities.Deduplicates || receiver.Capabilities.StatusLookup
                ? ExternalEffectOutcome.RetryLater : ExternalEffectOutcome.ReconciliationRequired;
        }
    }
}
