namespace Bsync.Blobs;

/// <summary>One bounded retired-file cleanup page; unknown and busy files advance the cursor but remain protected.</summary>
/// <param name="ExaminedFiles">Directory entries examined for reclamation.</param>
/// <param name="RemovedBytes">Physical payload bytes successfully reclaimed.</param>
/// <param name="Continuation">Last examined filename, or null at end. Restart at null to revisit protected files.</param>
public sealed record BlobRetiredCollectionResult(int ExaminedFiles, long RemovedBytes, string? Continuation);
