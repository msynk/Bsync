
namespace Bsync.Samples.Hybrid.Maui;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    // The session pauses and resumes with this window through Bsync.Maui (MauiProgram: UseMauiLifecycle).
    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new MainPage()) { Title = "Bsync notes (MAUI)" };
}
