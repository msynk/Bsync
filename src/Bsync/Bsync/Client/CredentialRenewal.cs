namespace Bsync.Client;

/// <summary>What <see cref="SyncSessionOptions{TDocument}.RenewCredentials"/> achieved (task D6).</summary>
public enum CredentialRenewal
{
    /// <summary>New credentials are in place; the session retries at once.</summary>
    Renewed,

    /// <summary>
    /// Renewal could not reach the identity provider (offline, timeout). Local saves and the upload queue are kept, the
    /// session reports <see cref="SyncState.Offline"/> and tries again with backoff. Never a reason to sign the user out.
    /// </summary>
    Offline,

    /// <summary>
    /// The user must sign in again (refresh token revoked or expired). Uploads stop and the session reports
    /// <see cref="SyncState.AttentionRequired"/>; local saves keep working and are uploaded after sign-in.
    /// </summary>
    SignInRequired,
}
