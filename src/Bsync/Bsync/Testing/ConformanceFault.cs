namespace Bsync.Testing;

/// <summary>Thrown by conformance cases to simulate a failure inside a transform.</summary>
public sealed class ConformanceFault(string message) : Exception(message);
