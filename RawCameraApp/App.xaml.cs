using RawCameraApp.Services.Imaging;

namespace RawCameraApp;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		_ = Task.Run(async () => await OnnxPhotonicEngine.InitializeAsync());
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}
}
