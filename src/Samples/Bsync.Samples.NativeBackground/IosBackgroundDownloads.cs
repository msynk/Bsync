#if IOS
using Foundation;

namespace Bsync.Samples.NativeBackground;

/// <summary>
/// One retained instance per account, recreated with the same identifier/inbox during AppDelegate launch, including
/// headless relaunch. OS-owned download bytes are plaintext; this opt-in recipe is separate from encrypted blob storage.
/// </summary>
public sealed class IosBackgroundDownloads
{
    private readonly BackgroundDownloadInbox _inbox;
    private readonly NSUrlSession _session;
    private readonly DownloadDelegate _delegate;
    private readonly NSOperationQueue _queue;
    private readonly SemaphoreSlim _scheduling = new(1, 1);
    private readonly Action<Exception> _report;
    private Action? _completion;
    private bool _invalidated;
    public string Identifier { get; }

    public IosBackgroundDownloads(string applicationPrefix, BackgroundDownloadInbox inbox, Action<Exception> report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationPrefix);
        _inbox = inbox; _report = report;
        Identifier = applicationPrefix + ".download." + inbox.Account.StorageKey;
        using var configuration = NSUrlSessionConfiguration.CreateBackgroundSessionConfiguration(Identifier);
        configuration.SessionSendsLaunchEvents = true;
        configuration.HttpCookieStorage = null;
        configuration.HttpShouldSetCookies = false;
        configuration.URLCredentialStorage = null;
        configuration.RequestCachePolicy = NSUrlRequestCachePolicy.ReloadIgnoringLocalCacheData;
        _delegate = new DownloadDelegate(this);
        _queue = new NSOperationQueue { MaxConcurrentOperationCount = 1 };
        _session = NSUrlSession.FromConfiguration(configuration, _delegate, _queue);
    }

    /// <summary>Forward the matching AppDelegate background URLSession completion handler; call on the main thread.</summary>
    public void HandleEvents(Action completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (Interlocked.CompareExchange(ref _completion, completion, null) is not null)
            throw new InvalidOperationException("A background event completion handler is already pending.");
    }

    /// <summary>
    /// Resume a durable receipt using an authorized, freshly issued HTTPS presigned GET ticket. No authority bearer
    /// credentials are attached. Enumerating OS tasks reconciles death between task creation and Resume.
    /// </summary>
    public async Task ResumeAsync(string id, Uri ticket, CancellationToken ct = default)
    {
        if (!ticket.IsAbsoluteUri || ticket.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(ticket.UserInfo) || !string.IsNullOrEmpty(ticket.Fragment))
            throw new ArgumentException("Expected an HTTPS presigned GET URL without user info or fragment.", nameof(ticket));
        await _scheduling.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_invalidated) throw new InvalidOperationException("The account's download session is invalidated.");
            var receipt = await _inbox.GetAsync(id, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("Prepare the durable receipt first.");
            if (receipt.Imported || _inbox.HasStagedFile(id)) return;
            var tasks = await _session.GetAllTasksAsync().ConfigureAwait(false);
            foreach (var orphan in tasks.Where(task => task.TaskDescription is null)) orphan.Cancel();
            var existing = tasks.Where(task => task.TaskDescription == id).ToArray();
            if (existing.Length > 0)
            {
                existing[0].Resume();
                foreach (var duplicate in existing.Skip(1)) duplicate.Cancel();
                return;
            }
            ct.ThrowIfCancellationRequested();
            using var url = new NSUrl(ticket.AbsoluteUri);
            using var request = new NSUrlRequest(url);
            var task = _session.CreateDownloadTask(request);
            task.TaskDescription = id;
            task.Resume();
        }
        finally { _scheduling.Release(); }
    }

    /// <summary>
    /// Cancels OS work before account deletion. The host must wait for the invalidation callback before deleting the
    /// inbox/catalog/spool and remove the account from its relaunch catalog so it cannot be scheduled again.
    /// </summary>
    public async Task InvalidateAsync()
    {
        await _scheduling.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_invalidated) { _invalidated = true; _session.InvalidateAndCancel(); }
        }
        finally { _scheduling.Release(); }
        await _delegate.Invalidated.Task.ConfigureAwait(false);
    }

    private void Report(Exception error)
    {
        try { _report(error); } catch (Exception) { /* Never unwind through an Objective-C callback. */ }
    }

    private sealed class DownloadDelegate(IosBackgroundDownloads owner) : NSUrlSessionDownloadDelegate
    {
        internal TaskCompletionSource Invalidated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void DidFinishDownloading(NSUrlSession session, NSUrlSessionDownloadTask downloadTask, NSUrl location)
        {
            try
            {
                var id = downloadTask.TaskDescription ?? throw new InvalidDataException("Missing durable download ID.");
                var status = downloadTask.Response is NSHttpUrlResponse response ? checked((int)response.StatusCode) : 0;
                // Apple's temporary file ceases to exist when this callback returns. Do not fire-and-forget this move.
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                owner._inbox.AcceptAsync(id, location.Path ?? throw new InvalidDataException("Missing download path."), status, deadline.Token)
                    .GetAwaiter().GetResult();
            }
            catch (Exception error) { owner.Report(error); }
        }

        public override void DidWriteData(NSUrlSession session, NSUrlSessionDownloadTask downloadTask,
            long bytesWritten, long totalBytesWritten, long totalBytesExpectedToWrite)
        {
            try
            {
                var id = downloadTask.TaskDescription ?? throw new InvalidDataException("Missing durable download ID.");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var receipt = owner._inbox.GetAsync(id, deadline.Token).GetAwaiter().GetResult();
                if (receipt is null || totalBytesWritten > receipt.Size || totalBytesExpectedToWrite > receipt.Size)
                    downloadTask.Cancel();
            }
            catch (Exception error) { downloadTask.Cancel(); owner.Report(error); }
        }

        public override void DidCompleteWithError(NSUrlSession session, NSUrlSessionTask task, NSError? error)
        {
            if (error is not null) owner.Report(new IOException("OS background download failed; its durable receipt remains pending."));
        }

        public override void DidFinishEventsForBackgroundSession(NSUrlSession session) =>
            BeginInvokeOnMainThread(() => Interlocked.Exchange(ref owner._completion, null)?.Invoke());

        public override void DidBecomeInvalid(NSUrlSession session, NSError? error) => Invalidated.TrySetResult();
    }
}
#endif
