using Bsync.Clocks;
using Bsync.Documents;
using Bsync.Server;

namespace Bsync.Tests.TestSupport;

public static class NoteJson
{
    public static readonly Func<Note, Note> Clone = DocumentCloner.Json(NoteJsonContext.Default.Note);

    public static readonly Func<Note, string> Fingerprint = DocumentCloner.JsonFingerprint(NoteJsonContext.Default.Note);

    public static InMemorySyncServerOptions<Note> ServerOptions(
        IPhysicalClock? clock = null,
        Func<SyncCallContext, Protocol.PushOperation<Note>, Note?, string?>? validator = null,
        int maxOperationsPerPush = 1000) =>
        new()
        {
            Cloner = Clone,
            Fingerprint = Fingerprint,
            PhysicalClock = clock ?? new ManualClock(1_000),
            Validator = validator,
            MaxOperationsPerPush = maxOperationsPerPush,
        };
}
