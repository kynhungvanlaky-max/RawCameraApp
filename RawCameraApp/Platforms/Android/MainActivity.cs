using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace RawCameraApp;

[Activity(Label = "RAW • PRO", Icon = "@mipmap/appicon", RoundIcon = "@mipmap/appicon_round", Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ScreenOrientation = ScreenOrientation.Portrait, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.OMr1)
        {
            SetShowWhenLocked(true);
            SetTurnScreenOn(true);
        }
        else
        {
            Window?.AddFlags(WindowManagerFlags.ShowWhenLocked | WindowManagerFlags.TurnScreenOn);
        }
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
        if (Intent != null)
        {
            new Android.OS.Handler(Android.OS.Looper.MainLooper!).PostDelayed(() =>
            {
                HandleTestIntent(Intent);
            }, 700);
        }
    }

    protected override void OnNewIntent(Android.Content.Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        HandleTestIntent(intent);
    }

    private void HandleTestIntent(Android.Content.Intent? intent)
    {
        if (intent == null) return;
        var mode = intent.GetStringExtra("mode");
        if (mode == "photo")
            MainPage.Instance?.TestSwitchMode(MainPage.CameraAppMode.Photo);
        else if (mode == "raw")
            MainPage.Instance?.TestSwitchMode(MainPage.CameraAppMode.RawPhoto);
        else if (mode == "ai")
            MainPage.Instance?.TestSwitchMode(MainPage.CameraAppMode.AiCam);
        else if (mode == "48mp")
            MainPage.Instance?.TestSwitchMode(MainPage.CameraAppMode.UltraHd48Mp);

        var action = intent.GetStringExtra("action");
        if (action == "capture")
        {
            MainPage.Instance?.TestCapture();
        }
        else if (action == "preview")
        {
            MainPage.Instance?.TestOpenPreview();
        }
        else if (action == "grid")
        {
            MainPage.Instance?.TestOpenGrid();
        }
        else if (action == "next")
        {
            MainPage.Instance?.TestNextPhoto();
        }
        else if (action == "prev")
        {
            MainPage.Instance?.TestPrevPhoto();
        }
        else if (action == "zoom")
        {
            MainPage.Instance?.TestToggleZoom();
        }
        else if (action == "close")
        {
            MainPage.Instance?.TestCloseModal();
        }
        else if (action == "processing")
        {
            MainPage.Instance?.TestSimulateProcessing();
        }
        else if (action == "lens_uw")
        {
            MainPage.Instance?.TestSwitchLens(RawCameraApp.Services.Imaging.CameraLensType.UltraWide);
        }
        else if (action == "lens_1x")
        {
            MainPage.Instance?.TestSwitchLens(RawCameraApp.Services.Imaging.CameraLensType.WideMain);
        }
        else if (action == "lens_2x")
        {
            MainPage.Instance?.TestSwitchLens(RawCameraApp.Services.Imaging.CameraLensType.Telephoto);
        }
        else if (action == "lens_macro")
        {
            MainPage.Instance?.TestSwitchLens(RawCameraApp.Services.Imaging.CameraLensType.Macro);
        }
        else if (action == "flip")
        {
            MainPage.Instance?.TestFlipCamera();
        }
        else if (action == "flash")
        {
            MainPage.Instance?.TestToggleFlash();
        }
        else if (action == "torch")
        {
            MainPage.Instance?.TestToggleTorch();
        }
        else if (action == "ai_studio")
        {
            MainPage.Instance?.TestToggleAiStudio();
        }
        else if (action == "ai_automaster")
        {
            MainPage.Instance?.TestAiAutoMaster();
        }
        else if (action == "rotate")
        {
            MainPage.Instance?.TestRotatePhoto();
        }
        else if (action == "details")
        {
            MainPage.Instance?.TestPhotoDetails();
        }
        else if (action == "multi_select")
        {
            MainPage.Instance?.TestToggleMultiSelect();
        }
        else if (action == "mode_48mp")
        {
            MainPage.Instance?.TestSwitchMode(MainPage.CameraAppMode.UltraHd48Mp);
        }
        else if (action == "mode_ai")
        {
            MainPage.Instance?.TestSwitchMode(MainPage.CameraAppMode.AiCam);
        }
        else if (action == "mode_photo")
        {
            MainPage.Instance?.TestSwitchMode(MainPage.CameraAppMode.Photo);
        }
        else if (action == "mode_raw")
        {
            MainPage.Instance?.TestSwitchMode(MainPage.CameraAppMode.RawPhoto);
        }
        else if (action == "start_brush")
        {
            MainPage.Instance?.TestStartBrush();
        }
        else if (action == "execute_prompt")
        {
            string prompt = intent.GetStringExtra("prompt") ?? "Xóa vật thể này đi";
            MainPage.Instance?.TestExecutePrompt(prompt);
        }
    }

    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
    {
        if (keyCode == Keycode.VolumeDown || keyCode == Keycode.VolumeUp)
        {
            if (MainPage.Instance != null && !MainPage.Instance.IsGalleryModalOpen)
            {
                MainPage.Instance.TriggerHardwareShutter();
                return true;
            }
        }
        return base.OnKeyDown(keyCode, e);
    }
}
