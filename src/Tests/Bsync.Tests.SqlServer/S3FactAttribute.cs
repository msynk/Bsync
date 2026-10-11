using Xunit;

namespace Bsync.Tests.SqlServer;

/// <summary>
/// Skipped unless <c>BSYNC_S3</c> names an S3-compatible endpoint that accepts the keys <c>test</c>/<c>test</c>, for
/// example a local moto server (<c>moto_server -p 59000</c>, then <c>BSYNC_S3=http://127.0.0.1:59000</c>).
/// </summary>
public sealed class S3FactAttribute : FactAttribute
{
    public S3FactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BSYNC_S3")))
        {
            Skip = "Set BSYNC_S3 to an S3-compatible endpoint to run object storage tests.";
        }
    }
}
