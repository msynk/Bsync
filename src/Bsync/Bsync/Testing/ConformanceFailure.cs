namespace Bsync.Testing;

/// <summary>Thrown when a conformance expectation fails.</summary>
public sealed class ConformanceFailure(string message) : Exception(message);
