using System.Security.Cryptography;
using Bsync.Blobs;

internal static class BenchmarkIdentity
{
    // Embedded assemblies have no independently hashable deployed DLL. Record the executable instead;
    // the qualification manifest separately retains the exact input package and publication evidence.
#if BSYNC_EMBEDDED_ASSEMBLIES
    public static string? LibraryAssemblySha256 => null;
    public static string? ExecutableSha256 => Hash(Environment.ProcessPath ?? throw new IOException("Executable path unavailable."));
#else
    public static string? LibraryAssemblySha256 => Hash(typeof(ChunkedEncryptedBlobCache).Assembly.Location);
    public static string? ExecutableSha256 => null;
#endif
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
