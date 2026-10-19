using Android.App;
using Android.Runtime;
using Bsync.Samples.NativeBackground;

namespace Bsync.Samples.Background.Android;

[Application]
public sealed class MainApplication(nint handle, JniHandleOwnership ownership) : Application(handle, ownership)
{
    internal static HeadlessSampleHost Host { get; private set; } = null!;
    public override void OnCreate()
    {
        base.OnCreate();
        SQLitePCL.Batteries_V2.Init();
        Host = new(FilesDir!.AbsolutePath);
        Host.Initialize();
        // WorkManager's default AndroidX Startup initializer remains enabled. Hosts with a custom WorkerFactory
        // must delegate this stable worker class or construct AccountSyncWorker with its original Java identity.
    }
}
