using Bsync.Samples.NativeBackground;
using Foundation;
using UIKit;

namespace Bsync.Samples.Background.Apple;

[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    private HeadlessSampleHost _host = null!;
    private IosBackgroundScheduler _scheduler = null!;
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
            try { var result = await _host.RequestDemoAsync(_scheduler); button.SetTitle(result.ToString(), UIControlState.Normal); }
            catch (Exception error) { button.SetTitle(error.GetType().Name, UIControlState.Normal); }
        };
        controller.View.AddSubview(button); Window.RootViewController = controller; Window.MakeKeyAndVisible();
        return true;
    }
    public override async void OnActivated(UIApplication application)
    {
        try { if (NativeBackgroundRuntime.Current is { } runtime) await runtime.ReconcileAsync(TimeSpan.FromSeconds(10), CancellationToken.None); }
        catch (Exception) { /* Foreground fallback retries retained work on the next activation. */ }
    }
    // This host intentionally does not enable URLSession transfers. The separate download-inbox recipe requires
    // a reviewed plaintext-spool/file-protection policy and real device qualification. Background uploads unsupported.
}
