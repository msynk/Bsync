using Android.App;
using Android.OS;
using Android.Widget;
using Bsync.Samples.NativeBackground;

namespace Bsync.Samples.Background.Android;

[Activity(Label = "Bsync headless reference", MainLauncher = true, Exported = true)]
public sealed class MainActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var button = new Button(this) { Text = "Queue durable local sample work" };
        button.Click += async (_, _) =>
        {
            try { button.Text = (await MainApplication.Host.RequestDemoAsync(new AndroidBackgroundScheduler(ApplicationContext!))).ToString(); }
            catch (Exception error) { button.Text = error.GetType().Name; }
        };
        SetContentView(button);
    }
    protected override async void OnResume()
    {
        base.OnResume();
        try { if (NativeBackgroundRuntime.Current is { } runtime) await runtime.ReconcileAsync(TimeSpan.FromSeconds(10), CancellationToken.None); }
        catch (Exception) { /* Durable work remains available for the next foreground or OS run. */ }
    }
}
