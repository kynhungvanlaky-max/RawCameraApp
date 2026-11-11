using Camera.MAUI;
using Microsoft.Extensions.Logging;
using RawCameraApp.Services;

#if ANDROID
using RawCameraApp.Platforms.Android;
#endif

namespace RawCameraApp;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseMauiCameraView()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

#if ANDROID
		builder.Services.AddSingleton<IPhotoStorageService, AndroidPhotoStorageService>();
#endif
		builder.Services.AddSingleton<IImageProcessor, IPhoneStyleImageProcessor>();
		builder.Services.AddTransient<MainPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
