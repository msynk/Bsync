using Bsync.Samples.NativeBackground;
using Foundation;
using UIKit;

namespace Bsync.Samples.Background.Apple;

[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    private HeadlessSampleHost _host = null!;
    private IosBackgroundScheduler _scheduler = null!;
    private SampleDownloadRegistration? _downloadRegistration;
    private IosBackgroundDownloads? _downloads;
    private readonly SemaphoreSlim _downloadSetup = new(1, 1), _imports = new(1, 1);
    private bool _deleting;
    private Action? _pendingDownloadCompletion;
    public override UIWindow? Window { get; set; }
    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        SQLitePCL.Batteries_V2.Init();
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BsyncHeadless");
        _host = new(directory); _host.Initialize();
        _scheduler = new("com.bsync.samples.headless.processing");
        if (!_scheduler.Register()) throw new InvalidOperationException("Background processing registration failed.");
        // All headless initialization above precedes UI creation and runs on every relevant launch path.
        Window = new UIWindow(UIScreen.MainScreen.Bounds);
        var controller = new UIViewController();
        controller.View!.BackgroundColor = UIColor.SystemBackground;
        var button = new UIButton(UIButtonType.System) { Frame = new CoreGraphics.CGRect(20, 100, 340, 80) };
        button.SetTitle("Queue durable local sample work", UIControlState.Normal);
        button.TouchUpInside += async (_, _) =>
        {
            try { var result = await _host.RequestDemoAsync(_scheduler); await EnsureDownloadsAsync(); button.SetTitle(result.ToString(), UIControlState.Normal); }
            catch (Exception error) { button.SetTitle(error.GetType().Name, UIControlState.Normal); }
        };
        controller.View.AddSubview(button); Window.RootViewController = controller; Window.MakeKeyAndVisible();
        return true;
    }
    public override async void OnActivated(UIApplication application)
    {
        try
        {
            await EnsureDownloadsAsync(); await ImportDownloadsAsync();
            if (NativeBackgroundRuntime.Current is { } runtime) await runtime.ReconcileAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
        catch (Exception) { /* Foreground fallback retries retained work on the next activation. */ }
    }
    public override async void HandleEventsForBackgroundUrl(UIApplication application, string sessionIdentifier, Action completionHandler)
    {
        try
        {
            var expected = "com.bsync.samples.headless.download." + HeadlessSampleHost.DemoAccount.StorageKey;
            if (!_deleting && sessionIdentifier == expected)
            {
                if (_downloads is not null) { _downloads.HandleEvents(completionHandler); return; }
                // Install the completion before session recreation can deliver any delegate callbacks.
                if (_pendingDownloadCompletion is not null) throw new InvalidOperationException("A session completion is already pending.");
                _pendingDownloadCompletion = completionHandler;
                await EnsureDownloadsAsync();
                if (_downloads is not null) return;
                _pendingDownloadCompletion = null;
            }
            // Unknown/deleted identities never open an inbox, provision keys or recreate an account.
            using var configuration = NSUrlSessionConfiguration.CreateBackgroundSessionConfiguration(sessionIdentifier);
            var obsolete = NSUrlSession.FromConfiguration(configuration); obsolete.InvalidateAndCancel();
        }
        catch (Exception error)
        {
            if (ReferenceEquals(_pendingDownloadCompletion, completionHandler)) _pendingDownloadCompletion = null;
            Report(error);
        }
        completionHandler();
    }

    private async Task EnsureDownloadsAsync()
    {
        await _downloadSetup.WaitAsync();
        try
        {
            if (_deleting || _downloads is not null) return;
            var registration = await _host.OpenDownloadsAsync();
            if (registration is null) return;
            try
            {
                ProtectDownloadPath(_host.DownloadDirectory);
                foreach (var path in Directory.EnumerateFileSystemEntries(_host.DownloadDirectory, "*", SearchOption.AllDirectories)) ProtectDownloadPath(path);
                _downloadRegistration = registration;
                _downloads = new("com.bsync.samples.headless", registration.Inbox, Report, ProtectDownloadPath, ImportDownloadsAsync, _pendingDownloadCompletion);
                _pendingDownloadCompletion = null;
            }
            catch { _downloadRegistration = null; await registration.DisposeAsync(); throw; }
        }
        finally { _downloadSetup.Release(); }
    }

    // Explicit sample policy: OS download/spool and this demo cache contain plaintext. Protection permits access
    // after first unlock following boot, including subsequent locks; files are excluded from backup. This is not E2EE.
    private static void ProtectDownloadPath(string path)
    {
        if (!NSFileManager.DefaultManager.SetAttributes(new NSFileAttributes { ProtectionKey = NSFileProtection.CompleteUntilFirstUserAuthentication }, path))
            throw new IOException("Download file protection is unavailable.");
        using var url = NSUrl.FromFilename(path);
        using var excluded = NSNumber.FromBoolean(true);
        if (!url.SetResource(NSUrl.IsExcludedFromBackupKey, excluded)) throw new IOException("Download backup exclusion failed.");
    }

    private async Task ImportDownloadsAsync()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await _imports.WaitAsync(budget.Token);
        try { if (!_deleting && _downloadRegistration is { } registration) await registration.Inbox.DrainAsync(_host.OpenDownloadScopeAsync, budget.Token); }
        finally { _imports.Release(); }
    }

    /// <summary>Call with server-authorized immutable identity and a fresh credential-isolated HTTPS download ticket.</summary>
    public async Task<string> QueueDownloadAsync(string sha256, long size, string owner, Uri ticket)
    {
        await EnsureDownloadsAsync();
        await _downloadSetup.WaitAsync();
        try
        {
            if (_deleting || _downloadRegistration is null || _downloads is null) throw new InvalidOperationException("Register the account in the foreground first.");
            var id = await _downloadRegistration.Inbox.PrepareAsync(sha256, size, owner);
            await _downloads.ResumeAsync(id, ticket); return id;
        }
        finally { _downloadSetup.Release(); }
    }

    /// <summary>Refreshes a lost/expired OS request using its existing durable receipt and a newly authorized ticket.</summary>
    public async Task ResumeDownloadAsync(string id, Uri ticket)
    {
        await EnsureDownloadsAsync(); await _downloadSetup.WaitAsync();
        try
        {
            if (_deleting || _downloads is null) throw new InvalidOperationException("Account downloads are unavailable.");
            await _downloads.ResumeAsync(id, ticket);
        }
        finally { _downloadSetup.Release(); }
    }

    public async Task<IReadOnlyList<BackgroundDownloadReceipt>> GetDownloadsAsync()
    {
        await EnsureDownloadsAsync(); await _downloadSetup.WaitAsync();
        try { return _deleting || _downloadRegistration is null ? [] : await _downloadRegistration.Inbox.ListAsync(); }
        finally { _downloadSetup.Release(); }
    }

    /// <summary>Fences new account opens, cancels/joins URLSession work and imports, then deletes local download state.</summary>
    public async Task DeleteDemoAsync()
    {
        await _downloadSetup.WaitAsync();
        try
        {
            _deleting = true;
            await _host.DeleteDemoAsync(async () =>
            {
                if (_downloads is not null) await _downloads.InvalidateAsync();
                await _imports.WaitAsync();
                try { if (_downloadRegistration is not null) await _downloadRegistration.DisposeAsync(); _downloadRegistration = null; _downloads = null; }
                finally { _imports.Release(); }
            });
        }
        finally { _downloadSetup.Release(); }
    }

    private static void Report(Exception error) => System.Diagnostics.Debug.WriteLine("Bsync download: " + error.GetType().Name);
}
