using Camera.MAUI;
using RawCameraApp.Services;
using RawCameraApp.Services.Imaging;
using OperationCanceledException = System.OperationCanceledException;
#if ANDROID
using Android.OS;
using RawCameraApp.Platforms.Android;
#endif

namespace RawCameraApp;

public partial class MainPage : ContentPage
{
    public enum CameraAppMode
    {
        AiCam,
        Photo,
        UltraHd48Mp,
        RawPhoto,
        Pro
    }

    private readonly IImageProcessor _imageProcessor;
    private readonly IPhotoStorageService? _storageService;
    private readonly SemaphoreSlim _cameraLock = new(1, 1);
    private readonly MaskDrawable _maskDrawable = new();

    private int _currentCameraIndex = 0;
    private bool _isCapturing = false;
    private bool _isCameraStarted = false;
    private bool _isStartingCamera = false;
    private bool _hasPermission = false;
    private Camera.MAUI.FlashMode _flashMode = Camera.MAUI.FlashMode.Auto;
    private bool _torchEnabled = false;
    private int _timerSeconds = 0; // 0 = off, 3 = 3s, 10 = 10s
    private CameraAppMode _currentMode = CameraAppMode.AiCam;
    private IPhonePhotoStyle _currentStyle = IPhonePhotoStyle.Standard;

    // Phân hệ AI CAM
    private AiCameraMode _currentAiMode = AiCameraMode.Auto;
    private float _aiStrength = 0.75f;
    private AiTelemetrySnapshot? _latestAiTelemetry = null;
    private CancellationTokenSource? _aiLoopCts = null;
    private int _lastAppliedEvComp = 0;
    private float _lastFocusNormX = 0.5f;
    private float _lastFocusNormY = 0.45f;
    private double _pinchStartZoom = 1.0;

    // Cache last captured photo data for preview modal & gallery
    private byte[]? _lastPhotoBytes;
    private string _lastFileName = string.Empty;
    private string _lastFileSizeString = string.Empty;
    private string _lastCaptureBadge = "CAM AI • Tự động";
    private bool _isInitialLaunch = true;
    private CancellationTokenSource? _toastCts = null;
    private int _activeProcessingJobs = 0;
    private CancellationTokenSource? _processingAnimCts = null;

    // Gallery & photo management state
    private List<PhotoItem> _allPhotos = new();
    private List<PhotoItem> _filteredPhotos = new();
    private int _currentPhotoIndex = 0;
    private string _currentFilter = "All";

    // Hệ thống Đa Ống Kính (Multi-Lens Management)
    private List<CameraLensProfile> _backLenses = new();
    private List<CameraLensProfile> _frontLenses = new();
    private CameraLensProfile? _activeLensProfile = null;
    private CancellationTokenSource? _specPillCts = null;

    public static MainPage? Instance { get; private set; }

    public bool IsGalleryModalOpen => PhotoPreviewModal?.IsVisible == true;

    public void TriggerHardwareShutter()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!_isCapturing && !IsGalleryModalOpen)
            {
                OnShutterClicked(null, EventArgs.Empty);
            }
        });
    }

    public MainPage(IImageProcessor imageProcessor, IPhotoStorageService? storageService = null)
    {
        InitializeComponent();
        Instance = this;
        _imageProcessor = imageProcessor;
        _storageService = storageService;
        MaskCanvasView.Drawable = _maskDrawable;
    }

    public void TestSwitchMode(CameraAppMode mode)
    {
        MainThread.BeginInvokeOnMainThread(() => SetCameraMode(mode));
    }

    public void TestCapture()
    {
        MainThread.BeginInvokeOnMainThread(() => OnShutterClicked(null, EventArgs.Empty));
    }

    public void TestOpenPreview()
    {
        MainThread.BeginInvokeOnMainThread(() => OnThumbnailClicked(null, EventArgs.Empty));
    }

    public void TestOpenGrid()
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (!PhotoPreviewModal.IsVisible)
            {
                await OpenGalleryAsync(0);
            }
            SinglePhotoView.IsVisible = false;
            GalleryGridView.IsVisible = true;
            ApplyPhotoFilter(_currentFilter);
        });
    }

    public void TestNextPhoto()
    {
        MainThread.BeginInvokeOnMainThread(() => NavigatePhoto(1));
    }

    public void TestPrevPhoto()
    {
        MainThread.BeginInvokeOnMainThread(() => NavigatePhoto(-1));
    }

    public void TestToggleZoom()
    {
        MainThread.BeginInvokeOnMainThread(() => ToggleZoomQuick());
    }

    public void TestCloseModal()
    {
        MainThread.BeginInvokeOnMainThread(() => OnCloseModalClicked(null, EventArgs.Empty));
    }

    public void TestSwitchLens(CameraLensType type)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var target = _backLenses.FirstOrDefault(l => l.LensType == type);
            if (target != null) await SwitchToLensAsync(target);
        });
    }

    public void TestFlipCamera()
    {
        MainThread.BeginInvokeOnMainThread(() => OnSwitchCameraClicked(null, EventArgs.Empty));
    }

    public void TestToggleFlash()
    {
        MainThread.BeginInvokeOnMainThread(() => OnFlashToggleClicked(null, EventArgs.Empty));
    }

    public void TestToggleTorch()
    {
        MainThread.BeginInvokeOnMainThread(() => OnTorchToggleClicked(null, EventArgs.Empty));
    }

    public void TestToggleAiStudio()
    {
        MainThread.BeginInvokeOnMainThread(() => OnToggleAiStudioClicked(null, EventArgs.Empty));
    }

    public void TestAiAutoMaster()
    {
        MainThread.BeginInvokeOnMainThread(() => OnAiAutoMasterTapped(null, EventArgs.Empty));
    }

    public void TestRotatePhoto()
    {
        MainThread.BeginInvokeOnMainThread(() => OnRotatePhotoClicked(null, EventArgs.Empty));
    }

    public void TestPhotoDetails()
    {
        MainThread.BeginInvokeOnMainThread(() => OnPhotoDetailsClicked(null, EventArgs.Empty));
    }

    public void TestToggleMultiSelect()
    {
        MainThread.BeginInvokeOnMainThread(() => OnToggleMultiSelectClicked(null, EventArgs.Empty));
    }

    public void TestSimulateProcessing()
    {
        _ = Task.Run(async () =>
        {
            StartProcessingStateAnimation("AI Photonic đang xử lý...");
            await Task.Delay(2500);
            UpdateProcessingStageText("Đang lưu ảnh...");
            await Task.Delay(800);
            FinishProcessingStateAnimation(true);
            ShowToast("Đã lưu • CAM AI • Tự động 100%");
        });
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        HookWindowLifecycle();
        await EnsurePermissionsAsync();
        SetCameraMode(CameraAppMode.AiCam);
        _ = CheckAndStartCameraAsync();
        _ = LoadLatestThumbnailAsync();
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        await StopCameraSafeAsync();
    }

    private bool _lifecycleHooked = false;
    private void HookWindowLifecycle()
    {
        if (_lifecycleHooked || Window == null) return;
        _lifecycleHooked = true;
        Window.Stopped -= OnWindowStopped;
        Window.Stopped += OnWindowStopped;
        Window.Resumed -= OnWindowResumed;
        Window.Resumed += OnWindowResumed;
    }

    private void OnWindowStopped(object? sender, EventArgs e)
    {
        _ = StopCameraSafeAsync();
    }

    private async void OnWindowResumed(object? sender, EventArgs e)
    {
        await StopCameraSafeAsync();
        _isCameraStarted = false;
        _ = CheckAndStartCameraAsync();
        _ = LoadLatestThumbnailAsync();
    }

    private async Task StopCameraSafeAsync()
    {
        StopRealTimeAiLoop();
        if (!await _cameraLock.WaitAsync(200)) return;
        try
        {
            _isCameraStarted = false;
            await CameraViewControl.StopCameraAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StopCameraSafeAsync] {ex}");
        }
        finally
        {
            _cameraLock.Release();
        }
    }

    private async Task EnsurePermissionsAsync()
    {
        var cameraStatus = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (cameraStatus != PermissionStatus.Granted)
        {
            cameraStatus = await Permissions.RequestAsync<Permissions.Camera>();
        }

        _hasPermission = (cameraStatus == PermissionStatus.Granted);
        if (!_hasPermission)
        {
            await DisplayAlert("Quyền truy cập", "Ứng dụng cần quyền Camera để chụp ảnh.", "OK");
        }
    }

    private void OnCameraViewLoaded(object? sender, EventArgs e)
    {
        HookWindowLifecycle();
        _ = CheckAndStartCameraAsync();
    }

    private void OnCamerasLoaded(object? sender, EventArgs e)
    {
        _ = CheckAndStartCameraAsync();
    }

    private void OnCameraViewSizeChanged(object? sender, EventArgs e)
    {
        if (ViewfinderContainer.Width > 0)
        {
            double targetHeight = ViewfinderContainer.Width * 4.0 / 3.0;
            if (Math.Abs(ViewfinderContainer.HeightRequest - targetHeight) > 1)
            {
                ViewfinderContainer.HeightRequest = targetHeight;
            }
        }

        _ = CheckAndStartCameraAsync();
#if ANDROID
        FixAndroidCameraTransform();
#endif
    }

    private async Task CheckAndStartCameraAsync()
    {
        if (!_hasPermission) return;
        if (_isCameraStarted || _isStartingCamera) return;

        if (!await _cameraLock.WaitAsync(50)) return;

        try
        {
            if (_isCameraStarted) return;
            _isStartingCamera = true;

            // Chờ danh sách camera sẵn sàng (tối đa 2.5s)
            int retries = 0;
            while (CameraViewControl.Cameras.Count == 0 && retries < 25)
            {
                await Task.Delay(100);
                retries++;
            }

            if (CameraViewControl.Cameras.Count == 0) return;

            // Chờ Viewfinder có kích thước hợp lệ trên màn hình (tối đa 2.5s)
            retries = 0;
            while ((CameraViewControl.Width < 50 || CameraViewControl.Height < 50) && retries < 25)
            {
                await Task.Delay(100);
                retries++;
            }

            // Khám phá phần cứng toàn bộ ống kính đa thiết bị
#if ANDROID
            if (_backLenses.Count == 0 && _frontLenses.Count == 0)
            {
                var (bLenses, fLenses) = CameraHardwareProfiler.DiscoverLenses(CameraViewControl.Cameras);
                _backLenses = bLenses;
                _frontLenses = fLenses;
            }
#endif

            // Ưu tiên chọn Camera Sau chính (WideMain 1x)
            if (CameraViewControl.Camera == null)
            {
                _activeLensProfile = _backLenses.FirstOrDefault(l => l.LensType == CameraLensType.WideMain) 
                                     ?? _backLenses.FirstOrDefault();

                if (_activeLensProfile?.MauiCamera != null)
                {
                    CameraViewControl.Camera = _activeLensProfile.MauiCamera;
                }
                else
                {
                    _currentCameraIndex = 0;
                    for (int i = 0; i < CameraViewControl.Cameras.Count; i++)
                    {
                        if (CameraViewControl.Cameras[i].Position == CameraPosition.Back)
                        {
                            _currentCameraIndex = i;
                            break;
                        }
                    }
                    CameraViewControl.Camera = CameraViewControl.Cameras[_currentCameraIndex];
                }
            }
            else if (_activeLensProfile == null)
            {
                _activeLensProfile = _backLenses.FirstOrDefault(l => l.MauiCamera == CameraViewControl.Camera)
                                     ?? _frontLenses.FirstOrDefault(l => l.MauiCamera == CameraViewControl.Camera)
                                     ?? _backLenses.FirstOrDefault();
            }

            // MirroredImage luôn là false để TakePhotoAsync chụp ảnh RAW gốc siêu nhanh và không bị xoay ảnh kép
            // Việc lật gương cho kính ngắm Selfie được xử lý trực tiếp trên Matrix của TextureView
            CameraViewControl.MirroredImage = false;

            // Khởi động Camera stream
            var result = await CameraViewControl.StartCameraAsync();
            if (result != CameraResult.Success)
            {
                // Thử lại sau 500ms phòng trường hợp hệ thống vừa mở khoá màn hình và HAL chưa sẵn sàng
                await Task.Delay(500);
                result = await CameraViewControl.StartCameraAsync();
            }

            if (result == CameraResult.Success)
            {
                _isCameraStarted = true;
                CameraViewControl.FlashMode = Camera.MAUI.FlashMode.Disabled;
                CameraViewControl.TorchEnabled = _torchEnabled;

                UpdateLensButtonsUi();
                UpdateLensTelemetryUi();

#if ANDROID
                SetupNativeAndroidFix();
                await Task.Delay(80);
                FixAndroidCameraTransform();
                if (!_torchEnabled) EnsureHardwareAeModeOn();
#endif
                if (_currentMode == CameraAppMode.AiCam || _currentMode == CameraAppMode.Photo)
                {
                    StartRealTimeAiLoop();
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[StartCameraAsync] Result: {result}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CheckAndStartCameraAsync] Error: {ex}");
        }
        finally
        {
            _isStartingCamera = false;
            _cameraLock.Release();
        }
    }

    private async void OnSwitchCameraClicked(object? sender, EventArgs e)
    {
        if (CameraViewControl.Cameras.Count < 2) return;
        if (!await _cameraLock.WaitAsync(250)) return;

        try
        {
            // Hiệu ứng xoay 180° & nảy đàn hồi nút đổi camera
            _ = SwitchCamIcon.RelRotateTo(180, 280, Easing.CubicOut);
            SwitchCamBorder.Scale = 0.85;
            _ = SwitchCamBorder.ScaleTo(1.0, 160, Easing.SpringOut);
            try { HapticFeedback.Default.Perform(HapticFeedbackType.Click); } catch { }

            var currentPos = CameraViewControl.Camera?.Position ?? CameraPosition.Back;
            bool goingToFront = (currentPos == CameraPosition.Back);

            CameraLensProfile? nextLens = null;
            if (goingToFront)
            {
                nextLens = _frontLenses.FirstOrDefault(l => l.LensType == CameraLensType.FrontSelfie) ?? _frontLenses.FirstOrDefault();
            }
            else
            {
                nextLens = _backLenses.FirstOrDefault(l => l.LensType == CameraLensType.WideMain) ?? _backLenses.FirstOrDefault();
            }

            await CameraViewControl.StopCameraAsync();
            _isCameraStarted = false;
            await Task.Delay(200);

            if (nextLens?.MauiCamera == null)
            {
                var targetPos = goingToFront ? CameraPosition.Front : CameraPosition.Back;
                var nextCamera = CameraViewControl.Cameras.FirstOrDefault(c => c.Position == targetPos)
                                 ?? CameraViewControl.Cameras.FirstOrDefault(c => c != CameraViewControl.Camera);
                if (nextCamera == null) return;
                CameraViewControl.Camera = nextCamera;
            }
            else
            {
                CameraViewControl.Camera = nextLens.MauiCamera;
                _activeLensProfile = nextLens;
            }

            CameraViewControl.ZoomFactor = 1.0f;
            CameraViewControl.MirroredImage = false;

            var result = await CameraViewControl.StartCameraAsync();
            if (result != CameraResult.Success)
            {
                await Task.Delay(300);
                result = await CameraViewControl.StartCameraAsync();
            }
            _isCameraStarted = (result == CameraResult.Success);

            CameraViewControl.FlashMode = Camera.MAUI.FlashMode.Disabled;
            CameraViewControl.TorchEnabled = _torchEnabled;

            UpdateLensButtonsUi();
            if (_activeLensProfile != null)
            {
                ShowLensSpecPill(_activeLensProfile);
                UpdateLensTelemetryUi();
            }

#if ANDROID
            await Task.Delay(80);
            FixAndroidCameraTransform();
            if (!_torchEnabled) EnsureHardwareAeModeOn();
#endif
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SwitchCameraError] {ex}");
        }
        finally
        {
            _cameraLock.Release();
        }
    }

#if ANDROID
    private void SetupNativeAndroidFix()
    {
        try
        {
            if (CameraViewControl.Handler?.PlatformView is Android.Views.ViewGroup vg)
            {
                vg.LayoutChange -= OnNativeLayoutChange;
                vg.LayoutChange += OnNativeLayoutChange;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SetupNativeAndroidFix] {ex}");
        }
    }

    private void OnNativeLayoutChange(object? sender, Android.Views.View.LayoutChangeEventArgs e)
    {
        FixAndroidCameraTransform();
    }

    private void FixAndroidCameraTransform()
    {
        try
        {
            if (CameraViewControl.Handler?.PlatformView is not Android.Views.ViewGroup vg) return;
            if (vg.Width <= 0 || vg.Height <= 0) return;

            Android.Views.TextureView? textureView = null;
            for (int i = 0; i < vg.ChildCount; i++)
            {
                if (vg.GetChildAt(i) is Android.Views.TextureView tv)
                {
                    textureView = tv;
                    break;
                }
            }

            if (textureView == null) return;

            float viewWidth = vg.Width;
            float viewHeight = vg.Height;

            // Kích thước chuẩn cảm biến 4:3 (ngắn x dài trong chế độ Portrait)
            float buffW = 1080f;
            float buffH = 1440f;

            try
            {
                var field = vg.GetType().GetField("videoSize",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field?.GetValue(vg) is Android.Util.Size size && size.Width > 0 && size.Height > 0)
                {
                    buffW = Math.Min(size.Width, size.Height);
                    buffH = Math.Max(size.Width, size.Height);
                }
            }
            catch { }

            var matrix = new Android.Graphics.Matrix();
            var viewRect = new Android.Graphics.RectF(0f, 0f, viewWidth, viewHeight);
            var buffRect = new Android.Graphics.RectF(0f, 0f, buffW, buffH);
            float cX = viewRect.CenterX();
            float cY = viewRect.CenterY();

            buffRect.Offset(cX - buffRect.CenterX(), cY - buffRect.CenterY());
            matrix.SetRectToRect(viewRect, buffRect, Android.Graphics.Matrix.ScaleToFit.Fill);

            // Căn chỉnh tỷ lệ 1:1 không bóp méo hình
            float scale = Math.Max(viewWidth / buffW, viewHeight / buffH);
            matrix.PostScale(scale, scale, cX, cY);

            // Nếu là Camera trước (Selfie), lật gương ngang trên Kính ngắm để người dùng ngắm tự nhiên
            if (CameraViewControl.Camera?.Position == CameraPosition.Front)
            {
                matrix.PostScale(-1f, 1f, cX, cY);
            }

            textureView.SetTransform(matrix);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FixAndroidCameraTransform] {ex}");
        }
    }

    private void ApplyHardwareAutoFocus(double touchX, double touchY, double viewW, double viewH)
    {
        try
        {
            if (CameraViewControl.Handler?.PlatformView is not Android.Views.ViewGroup vg) return;

            var pbField = vg.GetType().GetField("previewBuilder", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var psField = vg.GetType().GetField("previewSession", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            var ccField = vg.GetType().GetField("camChars", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (pbField?.GetValue(vg) is Android.Hardware.Camera2.CaptureRequest.Builder pb &&
                psField?.GetValue(vg) is Android.Hardware.Camera2.CameraCaptureSession ps)
            {
                if (ccField?.GetValue(vg) is Android.Hardware.Camera2.CameraCharacteristics chars)
                {
                    var activeArray = (Android.Graphics.Rect?)chars.Get(Android.Hardware.Camera2.CameraCharacteristics.SensorInfoActiveArraySize);
                    if (activeArray != null && viewW > 0 && viewH > 0)
                    {
                        int sensorOrientation = 90;
                        if (chars.Get(Android.Hardware.Camera2.CameraCharacteristics.SensorOrientation) is Java.Lang.Integer so)
                        {
                            sensorOrientation = so.IntValue();
                        }
                        bool isFront = (CameraViewControl.Camera?.Position == CameraPosition.Front);

                        double normX = Math.Clamp(touchX / viewW, 0.0, 1.0);
                        double normY = Math.Clamp(touchY / viewH, 0.0, 1.0);

                        // Ánh xạ xoay toạ độ màn hình dọc sang toạ độ cảm biến ngang (chuẩn 90/270 độ)
                        double sensorNormX, sensorNormY;
                        if (sensorOrientation == 90)
                        {
                            sensorNormX = normY;
                            sensorNormY = 1.0 - normX;
                        }
                        else if (sensorOrientation == 270)
                        {
                            sensorNormX = 1.0 - normY;
                            sensorNormY = normX;
                        }
                        else
                        {
                            sensorNormX = normX;
                            sensorNormY = normY;
                        }

                        if (isFront)
                        {
                            sensorNormY = 1.0 - sensorNormY;
                        }

                        int sensorX = (int)(sensorNormX * activeArray.Width());
                        int sensorY = (int)(sensorNormY * activeArray.Height());
                        int halfBox = Math.Min(activeArray.Width(), activeArray.Height()) / 12;

                        var focusRect = new Android.Graphics.Rect(
                            Math.Max(sensorX - halfBox, 0),
                            Math.Max(sensorY - halfBox, 0),
                            Math.Min(sensorX + halfBox, activeArray.Width()),
                            Math.Min(sensorY + halfBox, activeArray.Height())
                        );

                        var meteringRect = new Android.Hardware.Camera2.Params.MeteringRectangle(focusRect, Android.Hardware.Camera2.Params.MeteringRectangle.MeteringWeightMax);
                        pb.Set(Android.Hardware.Camera2.CaptureRequest.ControlAfRegions, new[] { meteringRect });
                        pb.Set(Android.Hardware.Camera2.CaptureRequest.ControlAeRegions, new[] { meteringRect });
                    }
                }

                // Kích hoạt AF tại vùng chạm
                pb.Set(Android.Hardware.Camera2.CaptureRequest.ControlAfMode, Java.Lang.Integer.ValueOf((int)Android.Hardware.Camera2.ControlAFMode.Auto));
                pb.Set(Android.Hardware.Camera2.CaptureRequest.ControlAfTrigger, Java.Lang.Integer.ValueOf((int)Android.Hardware.Camera2.ControlAFTrigger.Start));
                ps.SetRepeatingRequest(pb.Build(), null, null);

                // Sau 2.5s, tự động trả lại chế độ lấy nét liên tục (ContinuousPicture)
                Task.Delay(2500).ContinueWith(_ =>
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        try
                        {
                            pb.Set(Android.Hardware.Camera2.CaptureRequest.ControlAfTrigger, Java.Lang.Integer.ValueOf((int)Android.Hardware.Camera2.ControlAFTrigger.Idle));
                            pb.Set(Android.Hardware.Camera2.CaptureRequest.ControlAfMode, Java.Lang.Integer.ValueOf((int)Android.Hardware.Camera2.ControlAFMode.ContinuousPicture));
                            ps.SetRepeatingRequest(pb.Build(), null, null);
                        }
                        catch { }
                    });
                });
                return;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApplyHardwareAutoFocus] {ex}");
        }

        CameraViewControl.ForceAutoFocus();
    }

    private void EnsureHardwareAeModeOn()
    {
        try
        {
            if (CameraViewControl.Handler?.PlatformView is not Android.Views.ViewGroup vg) return;

            var pbField = vg.GetType().GetField("previewBuilder", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var psField = vg.GetType().GetField("previewSession", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            if (pbField?.GetValue(vg) is Android.Hardware.Camera2.CaptureRequest.Builder pb &&
                psField?.GetValue(vg) is Android.Hardware.Camera2.CameraCaptureSession ps)
            {
                pb.Set(Android.Hardware.Camera2.CaptureRequest.ControlAeMode,
                    Java.Lang.Integer.ValueOf((int)Android.Hardware.Camera2.ControlAEMode.On));
                ps.SetRepeatingRequest(pb.Build(), null, null);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EnsureHardwareAeModeOn] {ex.Message}");
        }
    }
#endif

    private void OnFlashToggleClicked(object? sender, EventArgs e)
    {
        // Tắt Torch nếu đang bật khi người dùng chỉnh Flash
        if (_torchEnabled)
        {
            _torchEnabled = false;
            CameraViewControl.TorchEnabled = false;
            TorchIconPath.Fill = Color.FromArgb("#88FFFFFF");
        }

        _flashMode = _flashMode switch
        {
            Camera.MAUI.FlashMode.Auto => Camera.MAUI.FlashMode.Enabled,
            Camera.MAUI.FlashMode.Enabled => Camera.MAUI.FlashMode.Disabled,
            _ => Camera.MAUI.FlashMode.Auto
        };

        switch (_flashMode)
        {
            case Camera.MAUI.FlashMode.Auto:
                FlashIconPath.Data = GetIconGeometry("IconFlashAutoData");
                FlashIconPath.Fill = Colors.White;
                ShowToast("Flash: Tự động");
                break;
            case Camera.MAUI.FlashMode.Enabled:
                FlashIconPath.Data = GetIconGeometry("IconFlashOnData");
                FlashIconPath.Fill = Color.FromArgb("#FFCC00");
                ShowToast("Flash: Bật");
                break;
            case Camera.MAUI.FlashMode.Disabled:
                FlashIconPath.Data = GetIconGeometry("IconFlashOffData");
                FlashIconPath.Fill = Color.FromArgb("#77FFFFFF");
                ShowToast("Flash: Tắt");
                break;
        }

        // Luôn giữ camera ở chế độ phơi sáng tự động liên tục (ControlAEMode.On) trong lúc ngắm
#if ANDROID
        EnsureHardwareAeModeOn();
#endif
    }

    private void OnTorchToggleClicked(object? sender, EventArgs e)
    {
        try
        {
            _torchEnabled = !_torchEnabled;
            CameraViewControl.TorchEnabled = _torchEnabled;
            TorchIconPath.Fill = _torchEnabled ? Color.FromArgb("#FFCC00") : Color.FromArgb("#88FFFFFF");

            // Khi bật Đèn pin, tạm tắt Flash để tránh xung đột phần cứng
            if (_torchEnabled)
            {
                _flashMode = Camera.MAUI.FlashMode.Disabled;
                CameraViewControl.FlashMode = _flashMode;
                FlashIconPath.Data = GetIconGeometry("IconFlashOffData");
                FlashIconPath.Fill = Color.FromArgb("#77FFFFFF");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TorchError] {ex}");
        }
    }

    private void OnGridToggleClicked(object? sender, EventArgs e)
    {
        RuleOfThirdsGrid.IsVisible = !RuleOfThirdsGrid.IsVisible;
        GridIconPath.Fill = RuleOfThirdsGrid.IsVisible ? Color.FromArgb("#FFCC00") : Color.FromArgb("#88FFFFFF");
    }

    private void OnTimerToggleClicked(object? sender, EventArgs e)
    {
        _timerSeconds = _timerSeconds switch
        {
            0 => 3,
            3 => 10,
            _ => 0
        };

        switch (_timerSeconds)
        {
            case 3:
                TimerIconPath.Data = GetIconGeometry("IconTimer3Data");
                TimerIconPath.Fill = Color.FromArgb("#FFCC00");
                break;
            case 10:
                TimerIconPath.Data = GetIconGeometry("IconTimer10Data");
                TimerIconPath.Fill = Color.FromArgb("#FFCC00");
                break;
            default:
                TimerIconPath.Data = GetIconGeometry("IconTimerData");
                TimerIconPath.Fill = Color.FromArgb("#88FFFFFF");
                break;
        }
    }

    private async void OnZoomUwClicked(object? sender, EventArgs e)
    {
        if (_currentMode == CameraAppMode.UltraHd48Mp)
        {
            ShowToast("Chế độ 48 MP chỉ hỗ trợ cảm biến chính 1x");
            return;
        }
        var target = _backLenses.FirstOrDefault(l => l.LensType == CameraLensType.UltraWide);
        if (target != null) await SwitchToLensAsync(target);
    }

    private async void OnZoom1xClicked(object? sender, EventArgs e)
    {
        var isFront = (CameraViewControl.Camera?.Position == CameraPosition.Front);
        var target = isFront
            ? (_frontLenses.FirstOrDefault(l => l.LensType == CameraLensType.FrontSelfie) ?? _frontLenses.FirstOrDefault())
            : (_backLenses.FirstOrDefault(l => l.LensType == CameraLensType.WideMain) ?? _backLenses.FirstOrDefault());
        if (target != null) await SwitchToLensAsync(target);
    }

    private async void OnZoom2xClicked(object? sender, EventArgs e)
    {
        if (_currentMode == CameraAppMode.UltraHd48Mp)
        {
            ShowToast("Chế độ 48 MP chỉ hỗ trợ cảm biến chính 1x");
            return;
        }
        var target = _backLenses.FirstOrDefault(l => l.LensType == CameraLensType.Telephoto);
        if (target != null) await SwitchToLensAsync(target);
    }

    private async void OnZoomMacroClicked(object? sender, EventArgs e)
    {
        if (_currentMode == CameraAppMode.UltraHd48Mp)
        {
            ShowToast("Chế độ 48 MP chỉ hỗ trợ cảm biến chính 1x");
            return;
        }
        var target = _backLenses.FirstOrDefault(l => l.LensType == CameraLensType.Macro);
        if (target != null) await SwitchToLensAsync(target);
    }

    public async Task SwitchToLensAsync(CameraLensProfile targetLens)
    {
        if (targetLens == null) return;
        if (!await _cameraLock.WaitAsync(350)) return;

        try
        {
            try { HapticFeedback.Default.Perform(HapticFeedbackType.Click); } catch { }

            bool isSameCameraDevice = (targetLens.MauiCamera != null && targetLens.MauiCamera == CameraViewControl.Camera);

            if (isSameCameraDevice)
            {
                // Cùng 1 cảm biến vật lý (ví dụ: chuyển 1x <-> 2x In-Sensor Crop hoặc Logical Multi-Camera)
                _activeLensProfile = targetLens;

#if ANDROID
                if (targetLens.UsesLogicalZoomRatio)
                {
                    ApplyHardwareZoomRatio(targetLens.TargetHardwareZoom);
                }
                else
#endif
                {
                    SetZoom(targetLens.TargetHardwareZoom);
                }

                UpdateLensButtonsUi();
                ShowLensSpecPill(targetLens);
                UpdateLensTelemetryUi();
                return;
            }

            // Khác Camera vật lý (ví dụ: chuyển từ 1x sang 0.6x Ultra-Wide hoặc Macro)
            if (targetLens.MauiCamera == null) return;

            await CameraViewControl.StopCameraAsync();
            _isCameraStarted = false;
            await Task.Delay(250); // Cho phép HAL giải phóng sensor clock và ISP pipeline

            CameraViewControl.ZoomFactor = 1.0f;
            CameraViewControl.Camera = targetLens.MauiCamera;
            CameraViewControl.MirroredImage = false;
            _activeLensProfile = targetLens;

            var result = await CameraViewControl.StartCameraAsync();
            if (result != CameraResult.Success)
            {
                await Task.Delay(350);
                result = await CameraViewControl.StartCameraAsync();
            }

            if (result == CameraResult.Success)
            {
                _isCameraStarted = true;
                CameraViewControl.FlashMode = Camera.MAUI.FlashMode.Disabled;
                CameraViewControl.TorchEnabled = _torchEnabled;

#if ANDROID
                await Task.Delay(80);
                FixAndroidCameraTransform();
                if (!_torchEnabled) EnsureHardwareAeModeOn();
#endif
                UpdateLensButtonsUi();
                ShowLensSpecPill(targetLens);
                UpdateLensTelemetryUi();
            }
            else
            {
                // An toàn: Nếu ống kính phụ bị lỗi HAL, tự động quay lại ống kính chính 1x
                System.Diagnostics.Debug.WriteLine($"[SwitchToLensAsync] Failed to open lens {targetLens.ShortBadge}. Fallback to 1x.");
                var fallback = _backLenses.FirstOrDefault(l => l.LensType == CameraLensType.WideMain);
                if (fallback?.MauiCamera != null && fallback.MauiCamera != targetLens.MauiCamera)
                {
                    CameraViewControl.Camera = fallback.MauiCamera;
                    _activeLensProfile = fallback;
                    await CameraViewControl.StartCameraAsync();
                    _isCameraStarted = true;
                    UpdateLensButtonsUi();
                    ShowToast($"Không thể mở {targetLens.DisplayName}, đã quay lại 1x");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SwitchToLensAsync] Error: {ex}");
        }
        finally
        {
            _cameraLock.Release();
        }
    }

#if ANDROID
    private void ApplyHardwareZoomRatio(float zoomRatio)
    {
        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                if (CameraViewControl.Handler?.PlatformView is Android.Views.ViewGroup vg)
                {
                    var pbField = vg.GetType().GetField("previewBuilder", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var psField = vg.GetType().GetField("previewSession", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (pbField?.GetValue(vg) is Android.Hardware.Camera2.CaptureRequest.Builder pb &&
                        psField?.GetValue(vg) is Android.Hardware.Camera2.CameraCaptureSession ps)
                    {
                        pb.Set(Android.Hardware.Camera2.CaptureRequest.ControlZoomRatio, Java.Lang.Float.ValueOf(zoomRatio));
                        ps.SetRepeatingRequest(pb.Build(), null, null);
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApplyHardwareZoomRatio] {ex.Message}");
        }

        SetZoom(zoomRatio);
    }
#endif

    private void UpdateLensButtonsUi()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            bool isFront = (CameraViewControl.Camera?.Position == CameraPosition.Front);

            if (isFront)
            {
                ZoomUwBtn.IsVisible = false;
                Zoom2xBtn.IsVisible = false;
                ZoomMacroBtn.IsVisible = false;

                Zoom1xBtn.IsVisible = true;
                Zoom1xBtn.BackgroundColor = Color.FromArgb("#FFCC00");
                Zoom1xLabel.Text = "1x";
                Zoom1xLabel.TextColor = Colors.Black;
                return;
            }

            // Chế độ Siêu phân giải 48 MP: Cảm biến IMX582 cố định 1x, ẩn các ống kính không tương thích
            if (_currentMode == CameraAppMode.UltraHd48Mp)
            {
                ZoomUwBtn.IsVisible = false;
                Zoom2xBtn.IsVisible = false;
                ZoomMacroBtn.IsVisible = false;

                Zoom1xBtn.IsVisible = true;
                Zoom1xBtn.BackgroundColor = Color.FromArgb("#FFB800");
                Zoom1xLabel.Text = "1x";
                Zoom1xLabel.TextColor = Colors.Black;
                return;
            }

            // Mặt sau: Hiển thị các nút tương ứng với ống kính thực tế máy có
            bool hasUw = _backLenses.Any(l => l.LensType == CameraLensType.UltraWide);
            bool hasMacro = _backLenses.Any(l => l.LensType == CameraLensType.Macro);

            ZoomUwBtn.IsVisible = hasUw;
            ZoomMacroBtn.IsVisible = hasMacro;
            Zoom1xBtn.IsVisible = true;
            Zoom2xBtn.IsVisible = true;

            var activeType = _activeLensProfile?.LensType ?? CameraLensType.WideMain;

            // Nút 0.6x (Ultra-Wide)
            if (hasUw)
            {
                var uwLens = _backLenses.FirstOrDefault(l => l.LensType == CameraLensType.UltraWide);
                ZoomUwLabel.Text = uwLens?.ShortBadge ?? "0.6x";
                bool isUwActive = (activeType == CameraLensType.UltraWide);
                ZoomUwBtn.BackgroundColor = isUwActive ? Color.FromArgb("#FFCC00") : Colors.Transparent;
                ZoomUwLabel.TextColor = isUwActive ? Colors.Black : Colors.White;
            }

            // Nút 1x (Main Wide)
            bool is1xActive = (activeType == CameraLensType.WideMain);
            Zoom1xBtn.BackgroundColor = is1xActive ? Color.FromArgb("#FFCC00") : Colors.Transparent;
            Zoom1xLabel.TextColor = is1xActive ? Colors.Black : Colors.White;

            // Nút 2x (Telephoto / 2x Crop)
            bool is2xActive = (activeType == CameraLensType.Telephoto);
            Zoom2xBtn.BackgroundColor = is2xActive ? Color.FromArgb("#FFCC00") : Colors.Transparent;
            Zoom2xLabel.TextColor = is2xActive ? Colors.Black : Colors.White;

            // Nút Macro
            if (hasMacro)
            {
                bool isMacroActive = (activeType == CameraLensType.Macro);
                ZoomMacroBtn.BackgroundColor = isMacroActive ? Color.FromArgb("#FFCC00") : Colors.Transparent;
                ZoomMacroLabel.TextColor = isMacroActive ? Colors.Black : Colors.White;
            }
        });
    }

    private void ShowLensSpecPill(CameraLensProfile lens)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                _specPillCts?.Cancel();
                _specPillCts?.Dispose();
                _specPillCts = new CancellationTokenSource();
                var ct = _specPillCts.Token;

                ActiveLensSpecLabel.Text = $"{lens.DisplayName} • {lens.SpecSummary}";
                ActiveLensSpecPill.Opacity = 0;
                ActiveLensSpecPill.TranslationY = 10;
                ActiveLensSpecPill.IsVisible = true;

                _ = ActiveLensSpecPill.FadeTo(1.0, 160, Easing.CubicOut);
                await ActiveLensSpecPill.TranslateTo(0, 0, 160, Easing.CubicOut);

                await Task.Delay(2000, ct);
                if (ct.IsCancellationRequested) return;

                await ActiveLensSpecPill.FadeTo(0, 200, Easing.CubicIn);
                ActiveLensSpecPill.IsVisible = false;
            }
            catch (OperationCanceledException) { }
            catch { }
        });
    }

    private void UpdateLensTelemetryUi()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_activeLensProfile != null)
            {
                AiTelemetryLens.Text = $"Ống kính: {_activeLensProfile.DisplayName}";
                AiTelemetrySensor.Text = $"Cảm biến: {_activeLensProfile.SpecSummary}";
            }
        });
    }

    private void SetZoom(float targetZoom)
    {
        try
        {
            if (CameraViewControl.Camera != null)
            {
                float minZ = CameraViewControl.Camera.MinZoomFactor;
                float maxZ = CameraViewControl.Camera.MaxZoomFactor;
                targetZoom = Math.Clamp(targetZoom, minZ, maxZ);
            }

            CameraViewControl.ZoomFactor = targetZoom;
            UpdateZoomUi(targetZoom);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ZoomError] {ex}");
        }
    }

    private void UpdateZoomUi(float currentZoom)
    {
        UpdateLensButtonsUi();
    }

    private DateTime _lastTapTime = DateTime.MinValue;
    private float _hardwareEvCompensation = 0.0f;

    private void OnViewfinderTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            var now = DateTime.UtcNow;
            if ((now - _lastTapTime).TotalMilliseconds < 340)
            {
                // Cử chỉ chạm đúp (Double-Tap) -> Chuyển nhanh Zoom 1x <-> 2x
                _lastTapTime = DateTime.MinValue;
                ToggleZoomQuick();
                return;
            }
            _lastTapTime = now;

            var point = e.GetPosition(ViewfinderTouchOverlay);
            if (point.HasValue && ViewfinderTouchOverlay.Width > 0 && ViewfinderTouchOverlay.Height > 0)
            {
                _lastFocusNormX = (float)Math.Clamp(point.Value.X / ViewfinderTouchOverlay.Width, 0.05, 0.95);
                _lastFocusNormY = (float)Math.Clamp(point.Value.Y / ViewfinderTouchOverlay.Height, 0.05, 0.95);

                FocusReticleContainer.TranslationX = point.Value.X - (ViewfinderTouchOverlay.Width / 2);
                FocusReticleContainer.TranslationY = point.Value.Y - (ViewfinderTouchOverlay.Height / 2);

#if ANDROID
                ApplyHardwareAutoFocus(point.Value.X, point.Value.Y, ViewfinderTouchOverlay.Width, ViewfinderTouchOverlay.Height);
#else
                CameraViewControl.ForceAutoFocus();
#endif
            }
            else
            {
                _lastFocusNormX = 0.5f;
                _lastFocusNormY = 0.45f;
                FocusReticleContainer.TranslationX = 0;
                FocusReticleContainer.TranslationY = 0;
                CameraViewControl.ForceAutoFocus();
            }

            // Hiệu ứng khung lấy nét vàng & nháy kép xác nhận khóa nét (AF Lock Double-Blink)
            try { HapticFeedback.Default.Perform(HapticFeedbackType.Click); } catch { }
            FocusReticleContainer.Opacity = 1;
            FocusReticleContainer.Scale = 1.32;
            _ = MainThread.InvokeOnMainThreadAsync(async () =>
            {
                await FocusReticleContainer.ScaleTo(1.0, 140, Easing.CubicOut);
                await FocusReticleContainer.FadeTo(0.45, 65);
                await FocusReticleContainer.FadeTo(1.0, 65);
            });

            Task.Delay(1500).ContinueWith(_ =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    _ = FocusReticleContainer.FadeTo(0, 220);
                });
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TapFocusError] {ex}");
        }
    }

    private void ToggleZoomQuick()
    {
        var currentType = _activeLensProfile?.LensType ?? CameraLensType.WideMain;
        var targetType = (currentType == CameraLensType.WideMain) ? CameraLensType.Telephoto : CameraLensType.WideMain;
        var target = _backLenses.FirstOrDefault(l => l.LensType == targetType);
        if (target != null)
        {
            _ = SwitchToLensAsync(target);
        }
        else
        {
            float targetZoom = (CameraViewControl.ZoomFactor < 1.6f) ? 2.0f : 1.0f;
            SetZoom(targetZoom);
            try { HapticFeedback.Default.Perform(HapticFeedbackType.Click); } catch { }
            ShowToast($"Zoom: {targetZoom:0.0}x");
        }
    }

    private void OnViewfinderPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        try
        {
            if (e.StatusType == GestureStatus.Running)
            {
                // Vuốt ngón tay lên/xuống để bù sáng EV (-2.0 đến +2.0)
                float evDelta = (float)(-e.TotalY / 140.0);
                float newEv = Math.Clamp(_hardwareEvCompensation + evDelta * 0.1f, -2.0f, 2.0f);
                newEv = (float)Math.Round(newEv * 2.0f) / 2.0f; // Bước 0.5 EV
                
                if (Math.Abs(newEv - _hardwareEvCompensation) >= 0.25f)
                {
                    _hardwareEvCompensation = newEv;
                    FocusEvLabel.Text = $"{_hardwareEvCompensation:+0.0;-0.0;0.0}";
                    ApplyHardwareExposure(_hardwareEvCompensation);
                    FocusReticleContainer.Opacity = 1;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PanEvError] {ex}");
        }
    }

    private void ApplyHardwareExposure(float evBias)
    {
#if ANDROID
        try
        {
            if (CameraViewControl.Handler?.PlatformView is not Android.Views.ViewGroup vg) return;

            var pbField = vg.GetType().GetField("previewBuilder", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var psField = vg.GetType().GetField("previewSession", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            var ccField = vg.GetType().GetField("camChars", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (pbField?.GetValue(vg) is Android.Hardware.Camera2.CaptureRequest.Builder pb &&
                psField?.GetValue(vg) is Android.Hardware.Camera2.CameraCaptureSession ps &&
                ccField?.GetValue(vg) is Android.Hardware.Camera2.CameraCharacteristics chars)
            {
                var range = (Android.Util.Range?)chars.Get(Android.Hardware.Camera2.CameraCharacteristics.ControlAeCompensationRange);
                var step = (Android.Util.Rational?)chars.Get(Android.Hardware.Camera2.CameraCharacteristics.ControlAeCompensationStep);

                if (range != null && step != null && step.Numerator != 0)
                {
                    float stepVal = (float)step.Numerator / step.Denominator;
                    int minIdx = ((Java.Lang.Integer)range.Lower).IntValue();
                    int maxIdx = ((Java.Lang.Integer)range.Upper).IntValue();

                    int targetIdx = (int)Math.Round(evBias / stepVal);
                    targetIdx = Math.Clamp(targetIdx, minIdx, maxIdx);

                    pb.Set(Android.Hardware.Camera2.CaptureRequest.ControlAeExposureCompensation, Java.Lang.Integer.ValueOf(targetIdx));
                    ps.SetRepeatingRequest(pb.Build(), null, null);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApplyHardwareExposure] {ex.Message}");
        }
#endif
    }

    private void OnViewfinderSwipedLeft(object? sender, SwipedEventArgs e)
    {
        // Vuốt sang trái -> chuyển sang chế độ tiếp theo bên phải (AiCam -> Photo -> UltraHd48Mp -> RawPhoto)
        if (_currentMode == CameraAppMode.AiCam)
            SetCameraMode(CameraAppMode.Photo);
        else if (_currentMode == CameraAppMode.Photo)
            SetCameraMode(CameraAppMode.UltraHd48Mp);
        else if (_currentMode == CameraAppMode.UltraHd48Mp)
            SetCameraMode(CameraAppMode.RawPhoto);
        try { HapticFeedback.Default.Perform(HapticFeedbackType.Click); } catch { }
    }

    private void OnViewfinderSwipedRight(object? sender, SwipedEventArgs e)
    {
        // Vuốt sang phải -> chuyển sang chế độ trước đó bên trái (RawPhoto -> UltraHd48Mp -> Photo -> AiCam)
        if (_currentMode == CameraAppMode.RawPhoto)
            SetCameraMode(CameraAppMode.UltraHd48Mp);
        else if (_currentMode == CameraAppMode.UltraHd48Mp)
            SetCameraMode(CameraAppMode.Photo);
        else if (_currentMode == CameraAppMode.Photo)
            SetCameraMode(CameraAppMode.AiCam);
        try { HapticFeedback.Default.Perform(HapticFeedbackType.Click); } catch { }
    }

    private void OnViewfinderPinchUpdated(object? sender, PinchGestureUpdatedEventArgs e)
    {
        if (_currentMode == CameraAppMode.UltraHd48Mp) return;

        try
        {
            switch (e.Status)
            {
                case GestureStatus.Started:
                    _pinchStartZoom = CameraViewControl.ZoomFactor;
                    break;
                case GestureStatus.Running:
                    float newZoom = (float)(_pinchStartZoom * e.Scale);
                    float minZoom = CameraViewControl.MinZoomFactor > 0 ? CameraViewControl.MinZoomFactor : 1.0f;
                    float maxZoom = CameraViewControl.MaxZoomFactor > 0 ? CameraViewControl.MaxZoomFactor : 6.0f;
                    newZoom = Math.Clamp(newZoom, minZoom, maxZoom);
                    CameraViewControl.ZoomFactor = newZoom;
                    UpdateZoomUi(newZoom);
                    break;
                case GestureStatus.Completed:
                case GestureStatus.Canceled:
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PinchZoomError] {ex}");
        }
    }

    private bool EvaluateShouldFireFlash(bool isFront)
    {
        if (_torchEnabled) return false;
        if (_flashMode == Camera.MAUI.FlashMode.Disabled) return false;
        if (_flashMode == Camera.MAUI.FlashMode.Enabled) return true;

        // Chế độ FlashMode.Auto: Đánh giá quang học bối cảnh thực tế
        float meanLuma = _latestAiTelemetry?.MeanLuma ?? -1f;
        float shadowRatio = _latestAiTelemetry?.ShadowRatio ?? 0f;

#if ANDROID
        // Nếu ở chế độ RAW hoặc chưa có dữ liệu AI telemetry gần nhất:
        // Lấy mẫu tức thời 96x72 trực tiếp từ TextureView (chỉ mất ~2ms)
        if (_currentMode == CameraAppMode.RawPhoto || meanLuma < 0f)
        {
            try
            {
                if (CameraViewControl.Handler?.PlatformView is Android.Views.ViewGroup vg)
                {
                    for (int i = 0; i < vg.ChildCount; i++)
                    {
                        if (vg.GetChildAt(i) is Android.Views.TextureView tv && tv.IsAvailable && tv.Width > 0 && tv.Height > 0)
                        {
                            using var sample = tv.GetBitmap(96, 72);
                            if (sample != null)
                            {
                                var snapshot = AiSceneAnalyzer.AnalyzeFrame(sample);
                                meanLuma = snapshot.MeanLuma;
                                shadowRatio = snapshot.ShadowRatio;
                                _latestAiTelemetry = snapshot;
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[EvaluateShouldFireFlash] {ex.Message}");
            }
        }
#endif

        if (meanLuma < 0f)
        {
            meanLuma = 0.5f; // Fallback an toàn nếu không lấy được mẫu
        }

        // Tiêu chuẩn nhận diện thiếu sáng:
        // 1. Độ sáng trung bình toàn cảnh < 0.38 (kể cả khi kính ngắm đã đẩy ISO cao)
        // 2. Hoặc bóng tối chiếm tỉ lệ lớn: meanLuma < 0.45 và shadowRatio > 0.35
        bool isDarkScene = (meanLuma < 0.38f) || (meanLuma < 0.45f && shadowRatio > 0.35f);
        return isDarkScene;
    }

    private async void OnShutterClicked(object? sender, EventArgs e)
    {
        if (_isCapturing) return;
        _isCapturing = true;
        bool flashAssisted = false;
        bool isFront = (CameraViewControl.Camera?.Position == CameraPosition.Front);

        try
        {
            // Xử lý đếm ngược hẹn giờ nếu có
            if (_timerSeconds > 0)
            {
                CountdownOverlay.IsVisible = true;
                for (int s = _timerSeconds; s >= 1; s--)
                {
                    CountdownLabel.Text = s.ToString();
                    CountdownLabel.Scale = 1.35;
                    _ = CountdownLabel.ScaleTo(1.0, 200, Easing.CubicOut);
                    await Task.Delay(1000);
                }
                CountdownOverlay.IsVisible = false;
            }

            // Phản hồi rung cơ học haptic
            try
            {
                HapticFeedback.Default.Perform(HapticFeedbackType.Click);
            }
            catch { }

            // Đánh giá đồng bộ điều kiện nháy Flash (Unified Flash Capture Pipeline)
            bool shouldFireFlash = EvaluateShouldFireFlash(isFront);

            if (shouldFireFlash)
            {
                if (!isFront)
                {
                    // Camera Sau: Kích hoạt đèn LED phần cứng với thời gian hội tụ 3A chuẩn xác (320ms)
                    CameraViewControl.FlashMode = Camera.MAUI.FlashMode.Auto;
                    CameraViewControl.TorchEnabled = true;
                    flashAssisted = true;
                    await Task.Delay(320); // 320ms cho đèn LED đạt cực đại và cảm biến khóa AE/AWB hoàn hảo
                }
                else
                {
                    // Camera Trước (Selfie): Retina Screen Flash (Hắt sáng trắng ấm độ sáng cao từ màn hình)
                    CameraViewControl.FlashMode = Camera.MAUI.FlashMode.Disabled;
                    ShutterFlash.Color = Color.FromArgb("#FFF8EE");
                    ShutterFlash.Opacity = 0;
                    ShutterFlash.IsVisible = true;
                    await ShutterFlash.FadeTo(0.96, 120, Easing.CubicOut);
                    await Task.Delay(240); // 240ms để khuôn mặt nhận đủ lượng sáng từ màn hình
                }
            }
            else
            {
                CameraViewControl.FlashMode = Camera.MAUI.FlashMode.Disabled;
            }

            // Hiệu ứng Shutter Feedback: Màn trập cơ học đen (DSLR Black Shutter Blink) cho camera sau
            if (!isFront || !shouldFireFlash)
            {
                ShutterFlash.Color = Color.FromArgb("#000000");
                ShutterFlash.Opacity = 0;
                ShutterFlash.IsVisible = true;
                _ = ShutterFlash.FadeTo(0.75, 30).ContinueWith(_ =>
                {
                    MainThread.BeginInvokeOnMainThread(async () =>
                    {
                        await Task.Delay(20);
                        await ShutterFlash.FadeTo(0, 70);
                        ShutterFlash.IsVisible = false;
                    });
                });
            }

            _ = ShutterOuterRing.ScaleTo(0.92, 40).ContinueWith(_ => ShutterOuterRing.ScaleTo(1.0, 80));
            await ShutterCore.ScaleTo(0.85, 40);
            _ = ShutterCore.ScaleTo(1.0, 80);

            // Chụp ảnh từ Camera
            using var photoStream = await CameraViewControl.TakePhotoAsync(Camera.MAUI.ImageFormat.JPEG);

            // Thu dọn trạng thái Flash ngay sau khi cảm biến đã bắt được khung hình
            if (flashAssisted)
            {
                CameraViewControl.TorchEnabled = false;
                CameraViewControl.FlashMode = Camera.MAUI.FlashMode.Disabled;
                flashAssisted = false;
#if ANDROID
                EnsureHardwareAeModeOn();
#endif
            }

            if (isFront && shouldFireFlash)
            {
                await ShutterFlash.FadeTo(0, 160, Easing.CubicIn);
                ShutterFlash.IsVisible = false;
                ShutterFlash.Color = Color.FromArgb("#000000");
            }

            if (photoStream == null)
            {
                _isCapturing = false;
                ShowToast("Không nhận được dữ liệu ảnh");
                return;
            }

            using var memoryStream = new MemoryStream();
            await photoStream.CopyToAsync(memoryStream);
            byte[] rawBytes = memoryStream.ToArray();

            // Xác định chế độ chụp và thông số đi kèm
            var captureMode = _currentMode;
            var effectiveStyle = _currentStyle;
            var aiMode = _currentAiMode;
            var aiStrength = _aiStrength;
            var telemetry = _latestAiTelemetry;
            var focusNormX = _lastFocusNormX;
            var focusNormY = _lastFocusNormY;
            var activeLens = _activeLensProfile;

            // MỞ KHOÁ NGAY NÚT CHỤP ĐỂ NGƯỜI DÙNG CÓ THỂ CHỤP TIẾP TỨC THÌ (NON-BLOCKING)
            _isCapturing = false;

            // Xử lý và lưu trên luồng nền
            _ = Task.Run(async () => await ProcessAndSavePhotoAsync(rawBytes, captureMode, effectiveStyle, aiMode, aiStrength, telemetry, focusNormX, focusNormY, isFront, activeLens));
        }
        catch (Exception ex)
        {
            if (flashAssisted)
            {
                try
                {
                    CameraViewControl.TorchEnabled = false;
                    CameraViewControl.FlashMode = Camera.MAUI.FlashMode.Disabled;
#if ANDROID
                    EnsureHardwareAeModeOn();
#endif
                }
                catch { }
            }

            if (isFront)
            {
                try
                {
                    ShutterFlash.IsVisible = false;
                    ShutterFlash.Color = Color.FromArgb("#000000");
                }
                catch { }
            }

            ShowToast($"Lỗi: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[CaptureError] {ex}");
            _isCapturing = false;
        }
    }

    private void StartProcessingStateAnimation(string initialStageText)
    {
        int activeCount = Interlocked.Increment(ref _activeProcessingJobs);
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            ProcessingStatusLabel.Text = activeCount > 1
                ? $"{initialStageText} • {activeCount}"
                : initialStageText;

            ProcessingQueueCountLabel.Text = activeCount.ToString();
            ProcessingQueueBadge.IsVisible = true;
            ThumbnailProcessingOverlay.IsVisible = true;
            ThumbnailBorder.Stroke = Color.FromArgb("#FFCC00");
            ShutterOuterRing.Stroke = Color.FromArgb("#FFCC00");

            if (!ProcessingStatusPill.IsVisible)
            {
                bool hasTopPill = (AiScenePill.IsVisible || UltraHd48MpPill.IsVisible);
                ProcessingStatusPill.Margin = new Thickness(0, hasTopPill ? 54 : 16, 0, 0);
                ProcessingStatusPill.Opacity = 0;
                ProcessingStatusPill.TranslationY = -12;
                ProcessingStatusPill.IsVisible = true;
                _ = ProcessingStatusPill.FadeTo(1, 180, Easing.CubicOut);
                _ = ProcessingStatusPill.TranslateTo(0, 0, 180, Easing.CubicOut);
            }

            if (_processingAnimCts == null)
            {
                _processingAnimCts = new CancellationTokenSource();
                var ct = _processingAnimCts.Token;
                _ = Task.Run(async () =>
                {
                    while (!ct.IsCancellationRequested)
                    {
                        await MainThread.InvokeOnMainThreadAsync(async () =>
                        {
                            if (ct.IsCancellationRequested) return;
                            ProcessingSpinnerIcon.Rotation = 0;
                            ThumbnailSpinnerPath.Rotation = 0;
                            var spin1 = ProcessingSpinnerIcon.RotateTo(360, 900, Easing.Linear);
                            var spin2 = ThumbnailSpinnerPath.RotateTo(360, 900, Easing.Linear);
                            await Task.WhenAll(spin1, spin2);
                        });
                    }
                }, ct);
            }
            await Task.CompletedTask;
        });
    }

    private void UpdateProcessingStageText(string stageText)
    {
        int activeCount = Volatile.Read(ref _activeProcessingJobs);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            ProcessingStatusLabel.Text = activeCount > 1
                ? $"{stageText} ({activeCount})"
                : stageText;
        });
    }

    private void FinishProcessingStateAnimation(bool success)
    {
        int remaining = Interlocked.Decrement(ref _activeProcessingJobs);
        if (remaining < 0)
        {
            Interlocked.Exchange(ref _activeProcessingJobs, 0);
            remaining = 0;
        }

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (remaining > 0)
            {
                ProcessingQueueCountLabel.Text = remaining.ToString();
                return;
            }

            try
            {
                _processingAnimCts?.Cancel();
                _processingAnimCts?.Dispose();
            }
            catch { }
            finally
            {
                _processingAnimCts = null;
            }

            ThumbnailProcessingOverlay.IsVisible = false;
            ProcessingQueueBadge.IsVisible = false;
            ShutterOuterRing.Stroke = Colors.White;

            _ = ProcessingStatusPill.FadeTo(0, 180, Easing.CubicIn).ContinueWith(_ =>
            {
                MainThread.BeginInvokeOnMainThread(() => ProcessingStatusPill.IsVisible = false);
            });

            if (success)
            {
                // Hiệu ứng nảy đàn hồi (Elastic Pop) + nháy viền xanh ngọc khi hoàn tất xử lý ảnh
                ThumbnailBorder.Stroke = Color.FromArgb("#34D399");
                ThumbnailBorder.Scale = 0.68;
                await ThumbnailBorder.ScaleTo(1.16, 170, Easing.CubicOut);
                await ThumbnailBorder.ScaleTo(1.0, 140, Easing.SpringOut);
                ThumbnailBorder.Stroke = Color.FromArgb("#55FFFFFF");
            }
            else
            {
                ThumbnailBorder.Stroke = Color.FromArgb("#55FFFFFF");
            }
        });
    }

    private async Task ProcessAndSavePhotoAsync(
        byte[] rawBytes,
        CameraAppMode captureMode,
        IPhonePhotoStyle effectiveStyle,
        AiCameraMode aiMode,
        float aiStrength,
        AiTelemetrySnapshot? telemetry,
        float focusNormX,
        float focusNormY,
        bool mirrorHorizontal,
        CameraLensProfile? lensProfile)
    {
        string initialStage = captureMode switch
        {
            CameraAppMode.AiCam => "AI Photonic đang xử lý...",
            CameraAppMode.UltraHd48Mp => "Đang xử lý siêu phân giải 48 MP...",
            CameraAppMode.RawPhoto => "Đang xuất ảnh RAW...",
            _ => "Đang xử lý Tone Curve..."
        };
        StartProcessingStateAnimation(initialStage);

        try
        {
            // Pipeline xử lý ảnh tính toán (AI CAM / PHOTO Style / 48 MP / RAW Gốc)
            byte[] finalBytes;
            if (captureMode == CameraAppMode.UltraHd48Mp)
            {
                finalBytes = await _imageProcessor.ProcessUltraHd48MpAsync(rawBytes, mirrorHorizontal, lensProfile);
            }
            else if (captureMode == CameraAppMode.AiCam)
            {
                finalBytes = await _imageProcessor.ProcessAiAsync(rawBytes, aiMode, aiStrength, telemetry, focusNormX, focusNormY, mirrorHorizontal, lensProfile);
            }
            else if (captureMode == CameraAppMode.RawPhoto)
            {
                finalBytes = await _imageProcessor.ProcessAsync(rawBytes, IPhonePhotoStyle.RawOriginal, mirrorHorizontal, lensProfile);
            }
            else
            {
                finalBytes = await _imageProcessor.ProcessAsync(rawBytes, effectiveStyle, mirrorHorizontal, lensProfile);
            }

            UpdateProcessingStageText("Đang lưu ảnh...");

            // Lưu trực tiếp vào DCIM/RawCamera với timestamp mili-giây để chống trùng tên
            string prefix = captureMode switch
            {
                CameraAppMode.AiCam => "IMG_AI",
                CameraAppMode.UltraHd48Mp => "IMG_48MP",
                CameraAppMode.RawPhoto => "IMG_RAW",
                _ => "IMG_PHOTO"
            };
            string fileName = $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg";

            if (_storageService != null)
            {
                await _storageService.SaveToGalleryAsync(finalBytes, fileName);
            }

            _lastPhotoBytes = finalBytes;
            _lastFileName = fileName;

            string lensBadge = lensProfile?.ShortBadge ?? "1x";
            string badge = captureMode switch
            {
                CameraAppMode.AiCam => $"CAM AI • {lensBadge} • {GetAiModeName(aiMode)} {(int)(aiStrength * 100)}%",
                CameraAppMode.UltraHd48Mp => "48 MP • 1x • 8000×6000",
                CameraAppMode.RawPhoto => $"RAW GỐC • {lensBadge}",
                _ => $"PHOTO • {lensBadge} • {GetStyleName(effectiveStyle)}"
            };
            _lastCaptureBadge = badge;

            double sizeKb = Math.Round(finalBytes.Length / 1024.0, 1);
            _lastFileSizeString = sizeKb > 1024 ? $"{Math.Round(sizeKb / 1024.0, 2)} MB" : $"{sizeKb} KB";

            // Cập nhật Thumbnail góc trái + hoàn tất hiệu ứng xử lý
            MainThread.BeginInvokeOnMainThread(() =>
            {
                ThumbnailEmptyPath.IsVisible = false;
                LastCapturedPreview.IsVisible = true;
                LastCapturedPreview.Source = ImageSource.FromStream(() => new MemoryStream(finalBytes));

                FinishProcessingStateAnimation(true);
                ShowToast($"Đã lưu • {badge}");
            });
        }
        catch (Exception ex)
        {
            FinishProcessingStateAnimation(false);
            ShowToast($"Lỗi: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ProcessAndSavePhotoAsync] {ex}");
        }
    }

    private void ShowToast(string message)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                _toastCts?.Cancel();
                _toastCts?.Dispose();
                _toastCts = new CancellationTokenSource();
                var ct = _toastCts.Token;

                ToastLabel.Text = message;
                ToastNotification.Opacity = 0;
                ToastNotification.TranslationY = -14;
                ToastNotification.Scale = 0.92;
                ToastCheckIcon.Scale = 0.4;
                ToastNotification.IsVisible = true;

                _ = ToastNotification.FadeTo(1, 140, Easing.CubicOut);
                _ = ToastNotification.TranslateTo(0, 0, 160, Easing.CubicOut);
                await ToastNotification.ScaleTo(1.0, 160, Easing.SpringOut);
                _ = ToastCheckIcon.ScaleTo(1.0, 180, Easing.SpringOut);

                await Task.Delay(1600, ct);
                if (ct.IsCancellationRequested) return;

                _ = ToastNotification.TranslateTo(0, -10, 160, Easing.CubicIn);
                await ToastNotification.FadeTo(0, 160, Easing.CubicIn);
                ToastNotification.IsVisible = false;
            }
            catch (OperationCanceledException) { }
            catch { }
        });
    }

    private async Task LoadLatestThumbnailAsync()
    {
        try
        {
            if (_storageService == null) return;
            var photos = await _storageService.GetAllPhotosAsync();
            _allPhotos = photos ?? new List<PhotoItem>();

            var latest = await _storageService.GetLatestPhotoAsync();
            if (latest != null && latest.Bytes.Length > 0)
            {
                _lastPhotoBytes = latest.Bytes;
                _lastFileName = latest.FileName;
                _lastFileSizeString = latest.FileSizeString;

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    ThumbnailEmptyPath.IsVisible = false;
                    LastCapturedPreview.IsVisible = true;
                    LastCapturedPreview.Source = ImageSource.FromStream(() => new MemoryStream(_lastPhotoBytes));
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LoadLatestThumbnailAsync] {ex}");
        }
    }

    private void OnAiCameraModeTapped(object? sender, EventArgs e) => SetCameraMode(CameraAppMode.AiCam);
    private void OnPhotoModeTapped(object? sender, EventArgs e) => SetCameraMode(CameraAppMode.Photo);
    private void On48MpModeTapped(object? sender, EventArgs e) => SetCameraMode(CameraAppMode.UltraHd48Mp);
    private void OnRawPhotoModeTapped(object? sender, EventArgs e) => SetCameraMode(CameraAppMode.RawPhoto);

    private void SetCameraMode(CameraAppMode mode)
    {
        _currentMode = mode;

        ModeAiLabel.TextColor = (mode == CameraAppMode.AiCam) ? Color.FromArgb("#FFCC00") : Color.FromArgb("#77FFFFFF");
        ModeAiLabel.FontSize = (mode == CameraAppMode.AiCam) ? 13 : 12;
        ModeAiDot.IsVisible = (mode == CameraAppMode.AiCam);

        ModePhotoLabel.TextColor = (mode == CameraAppMode.Photo) ? Color.FromArgb("#FFCC00") : Color.FromArgb("#77FFFFFF");
        ModePhotoLabel.FontSize = (mode == CameraAppMode.Photo) ? 13 : 12;
        ModePhotoDot.IsVisible = (mode == CameraAppMode.Photo);

        Mode48MpLabel.TextColor = (mode == CameraAppMode.UltraHd48Mp) ? Color.FromArgb("#FFB800") : Color.FromArgb("#77FFFFFF");
        Mode48MpLabel.FontSize = (mode == CameraAppMode.UltraHd48Mp) ? 13 : 12;
        Mode48MpDot.IsVisible = (mode == CameraAppMode.UltraHd48Mp);

        ModeRawLabel.TextColor = (mode == CameraAppMode.RawPhoto) ? Color.FromArgb("#FFCC00") : Color.FromArgb("#77FFFFFF");
        ModeRawLabel.FontSize = (mode == CameraAppMode.RawPhoto) ? 13 : 12;
        ModeRawDot.IsVisible = (mode == CameraAppMode.RawPhoto);

        // Thanh chọn tính năng tương ứng từng chế độ + hiệu ứng trượt mượt
        AiControlsBar.IsVisible = (mode == CameraAppMode.AiCam);
        AiScenePill.IsVisible = (mode == CameraAppMode.AiCam);
        if (mode != CameraAppMode.AiCam)
        {
            AiStrengthFloatingBar.IsVisible = false;
        }
        StyleSelectorBar.IsVisible = (mode == CameraAppMode.Photo);
        UltraHd48MpPill.IsVisible = (mode == CameraAppMode.UltraHd48Mp);
        RawControlsBar.IsVisible = (mode == CameraAppMode.RawPhoto);

        if (mode == CameraAppMode.UltraHd48Mp)
        {
            if (_activeLensProfile?.LensType != CameraLensType.WideMain)
            {
                var target = _backLenses.FirstOrDefault(l => l.LensType == CameraLensType.WideMain);
                if (target != null) _ = SwitchToLensAsync(target);
            }
        }

        UpdateLensButtonsUi();

        VisualElement activeBar = mode switch
        {
            CameraAppMode.AiCam => AiControlsBar,
            CameraAppMode.Photo => StyleSelectorBar,
            CameraAppMode.UltraHd48Mp => UltraHd48MpPill,
            _ => RawControlsBar
        };
        VisualElement activeDot = mode switch
        {
            CameraAppMode.AiCam => ModeAiDot,
            CameraAppMode.Photo => ModePhotoDot,
            CameraAppMode.UltraHd48Mp => Mode48MpDot,
            _ => ModeRawDot
        };
        if (!_isInitialLaunch)
        {
            activeDot.Scale = 0.2;
            _ = activeDot.ScaleTo(1.3, 140, Easing.CubicOut).ContinueWith(_ =>
                MainThread.BeginInvokeOnMainThread(async () => await activeDot.ScaleTo(1.0, 100, Easing.SpringOut)));

            activeBar.Opacity = 0;
            activeBar.TranslationY = 8;
            _ = activeBar.FadeTo(1, 180, Easing.CubicOut);
            _ = activeBar.TranslateTo(0, 0, 180, Easing.CubicOut);
        }

        if (mode == CameraAppMode.AiCam || mode == CameraAppMode.Photo)
        {
            StartRealTimeAiLoop();
            if (mode != CameraAppMode.AiCam)
            {
                AiTelemetryOverlay.IsVisible = false;
            }
        }
        else
        {
            StopRealTimeAiLoop();
            AiTelemetryOverlay.IsVisible = false;
#if ANDROID
            ApplyHardwareExposureCompensation(0.0f);
#endif
        }

        if (_isInitialLaunch)
        {
            _isInitialLaunch = false;
        }
        else
        {
            string modeName = mode switch
            {
                CameraAppMode.AiCam => $"CAM AI • {GetAiModeName(_currentAiMode)}",
                CameraAppMode.Photo => $"CHỤP ẢNH • {GetStyleName(_currentStyle)}",
                CameraAppMode.UltraHd48Mp => "SIÊU PHÂN GIẢI 48 MP",
                _ => "ẢNH GỐC RAW"
            };
            ShowToast($"Chế độ: {modeName}");
        }
    }

    private void OnAiModeAutoTapped(object? sender, EventArgs e) => SetAiMode(AiCameraMode.Auto);
    private void OnAiModePortraitTapped(object? sender, EventArgs e) => SetAiMode(AiCameraMode.Portrait);
    private void OnAiModeNightTapped(object? sender, EventArgs e) => SetAiMode(AiCameraMode.NightSight);
    private void OnAiModeLandscapeTapped(object? sender, EventArgs e) => SetAiMode(AiCameraMode.Landscape);
    private void OnAiModeDetailTapped(object? sender, EventArgs e) => SetAiMode(AiCameraMode.SuperDetail);

    private void OnToggleAiStrengthTapped(object? sender, EventArgs e)
    {
        AiStrengthFloatingBar.IsVisible = !AiStrengthFloatingBar.IsVisible;
    }

    private void OnCloseAiStrengthTapped(object? sender, EventArgs e)
    {
        AiStrengthFloatingBar.IsVisible = false;
    }

    private void SetAiMode(AiCameraMode mode)
    {
        _currentAiMode = mode;

        UpdateStylePill(AiModeAutoPill, AiModeAutoLabel, AiModeAutoIcon, mode == AiCameraMode.Auto);
        UpdateStylePill(AiModePortraitPill, AiModePortraitLabel, AiModePortraitIcon, mode == AiCameraMode.Portrait);
        UpdateStylePill(AiModeNightPill, AiModeNightLabel, AiModeNightIcon, mode == AiCameraMode.NightSight);
        UpdateStylePill(AiModeLandscapePill, AiModeLandscapeLabel, AiModeLandscapeIcon, mode == AiCameraMode.Landscape);
        UpdateStylePill(AiModeDetailPill, AiModeDetailLabel, AiModeDetailIcon, mode == AiCameraMode.SuperDetail);

        ShowToast($"Chế độ AI: {GetAiModeName(mode)}");
    }

    private static string GetAiModeName(AiCameraMode mode) => mode switch
    {
        AiCameraMode.Auto => "Tự động",
        AiCameraMode.Portrait => "Chân dung",
        AiCameraMode.NightSight => "Chụp đêm",
        AiCameraMode.Landscape => "Phong cảnh",
        AiCameraMode.SuperDetail => "Siêu nét",
        _ => "Tự động"
    };

    private void OnAiStrengthSliderValueChanged(object? sender, ValueChangedEventArgs e)
    {
        _aiStrength = (float)e.NewValue / 100.0f;
        int pct = (int)Math.Round(e.NewValue);
        AiStrengthLabel.Text = $"AI: {pct}%";
        AiStrengthQuickLabel.Text = $"{pct}%";
    }

    private void OnAiScenePillTapped(object? sender, EventArgs e)
    {
        AiTelemetryOverlay.IsVisible = !AiTelemetryOverlay.IsVisible;
    }

    private void OnCloseAiTelemetryTapped(object? sender, EventArgs e)
    {
        AiTelemetryOverlay.IsVisible = false;
    }

    private void StartRealTimeAiLoop()
    {
        StopRealTimeAiLoop();
        _aiLoopCts = new CancellationTokenSource();
        _ = RunRealTimeAiLoopAsync(_aiLoopCts.Token);
    }

    private void StopRealTimeAiLoop()
    {
        try
        {
            _aiLoopCts?.Cancel();
            _aiLoopCts?.Dispose();
        }
        catch { }
        finally
        {
            _aiLoopCts = null;
        }
    }

    private async Task RunRealTimeAiLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && (_currentMode == CameraAppMode.AiCam || _currentMode == CameraAppMode.Photo))
        {
            try
            {
                await Task.Delay(450, ct);
                if (ct.IsCancellationRequested || !_isCameraStarted || _isCapturing) continue;

#if ANDROID
                Android.Graphics.Bitmap? sample = null;
                if (CameraViewControl.Handler?.PlatformView is Android.Views.ViewGroup vg)
                {
                    for (int i = 0; i < vg.ChildCount; i++)
                    {
                        if (vg.GetChildAt(i) is Android.Views.TextureView tv && tv.IsAvailable && tv.Width > 0 && tv.Height > 0)
                        {
                            sample = tv.GetBitmap(160, 120);
                            break;
                        }
                    }
                }

                if (sample != null)
                {
                    var snapshot = AiSceneAnalyzer.AnalyzeFrame(sample);
                    sample.Recycle();
                    _latestAiTelemetry = snapshot;

                    // Không can thiệp ghi đè repeatingRequest để giữ vững Auto-Exposure quang học gốc của Camera2 HAL
                    // Bù trừ sáng thông minh sẽ được tính toán trực tiếp trong Smart HDR Pipeline

                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (_currentMode != CameraAppMode.AiCam) return;

                        AiSceneIconPath.Data = GetIconGeometry(snapshot.SceneIcon);
                        AiSceneTextLabel.Text = snapshot.SceneName;
                        if (Math.Abs(snapshot.RecommendedEvShift) >= 0.3f)
                        {
                            AiEvShiftLabel.Text = $"EV {snapshot.RecommendedEvShift:+0.0;-0.0;0.0}";
                            AiEvShiftLabel.IsVisible = true;
                        }
                        else
                        {
                            AiEvShiftLabel.IsVisible = false;
                        }

                        if (AiTelemetryOverlay.IsVisible)
                        {
                            AiTelemetryLuma.Text = $"Luma: {(int)(snapshot.MeanLuma * 100)}%";
                            AiTelemetryContrast.Text = $"Tối: {(int)(snapshot.ShadowRatio * 100)}% | Sáng: {(int)(snapshot.HighlightRatio * 100)}%";
                            AiTelemetrySkin.Text = $"Da người: {(int)(snapshot.SkinCoverage * 100)}%";
                            AiTelemetrySky.Text = $"Bầu trời: {(int)(snapshot.SkyCoverage * 100)}%";
                            AiTelemetryFoliage.Text = $"Cây cối: {(int)(snapshot.FoliageCoverage * 100)}%";
                            AiTelemetryHardwareEv.Text = $"EV Auto: {snapshot.RecommendedEvShift:+0.0;-0.0;0.0}";
                            if (_activeLensProfile != null)
                            {
                                AiTelemetryLens.Text = $"Ống kính: {_activeLensProfile.DisplayName}";
                                AiTelemetrySensor.Text = $"Cảm biến: {_activeLensProfile.SpecSummary}";
                            }
                        }
                    });
                }
#endif
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RunRealTimeAiLoopAsync] {ex}");
            }
        }
    }

#if ANDROID
    private void ApplyHardwareExposureCompensation(float evShift)
    {
        try
        {
            if (CameraViewControl.Handler?.PlatformView is not Android.Views.ViewGroup vg) return;

            var pbField = vg.GetType().GetField("previewBuilder", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var psField = vg.GetType().GetField("previewSession", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            var ccField = vg.GetType().GetField("camChars", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (pbField?.GetValue(vg) is Android.Hardware.Camera2.CaptureRequest.Builder pb &&
                psField?.GetValue(vg) is Android.Hardware.Camera2.CameraCaptureSession ps &&
                ccField?.GetValue(vg) is Android.Hardware.Camera2.CameraCharacteristics chars)
            {
                var stepRange = (Android.Util.Range?)chars.Get(Android.Hardware.Camera2.CameraCharacteristics.ControlAeCompensationRange);
                var stepRational = (Android.Util.Rational?)chars.Get(Android.Hardware.Camera2.CameraCharacteristics.ControlAeCompensationStep);
                if (stepRange != null && stepRational != null && stepRational.Numerator != 0)
                {
                    float stepSize = (float)stepRational.Numerator / stepRational.Denominator;
                    int targetIndex = (int)Math.Round(evShift / stepSize);
                    int lower = ((Java.Lang.Integer)stepRange.Lower).IntValue();
                    int upper = ((Java.Lang.Integer)stepRange.Upper).IntValue();
                    targetIndex = Math.Clamp(targetIndex, lower, upper);

                    if (targetIndex != _lastAppliedEvComp)
                    {
                        _lastAppliedEvComp = targetIndex;
                        pb.Set(Android.Hardware.Camera2.CaptureRequest.ControlAeExposureCompensation, Java.Lang.Integer.ValueOf(targetIndex));
                        ps.SetRepeatingRequest(pb.Build(), null, null);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApplyHardwareExposureCompensation] {ex}");
        }
    }
#endif

    private void OnStyleStandardTapped(object? sender, EventArgs e) => SetPhotographicStyle(IPhonePhotoStyle.Standard);
    private void OnStyleWarmTapped(object? sender, EventArgs e) => SetPhotographicStyle(IPhonePhotoStyle.Warm);
    private void OnStyleVibrantTapped(object? sender, EventArgs e) => SetPhotographicStyle(IPhonePhotoStyle.Vibrant);
    private void OnStyleRichContrastTapped(object? sender, EventArgs e) => SetPhotographicStyle(IPhonePhotoStyle.RichContrast);
    private void OnStyleCoolTapped(object? sender, EventArgs e) => SetPhotographicStyle(IPhonePhotoStyle.Cool);

    private void SetPhotographicStyle(IPhonePhotoStyle style)
    {
        _currentStyle = style;

        UpdateStylePill(StyleStandardPill, StyleStandardLabel, StyleStandardIcon, style == IPhonePhotoStyle.Standard);
        UpdateStylePill(StyleWarmPill, StyleWarmLabel, StyleWarmIcon, style == IPhonePhotoStyle.Warm);
        UpdateStylePill(StyleVibrantPill, StyleVibrantLabel, StyleVibrantIcon, style == IPhonePhotoStyle.Vibrant);
        UpdateStylePill(StyleRichContrastPill, StyleRichContrastLabel, StyleRichContrastIcon, style == IPhonePhotoStyle.RichContrast);
        UpdateStylePill(StyleCoolPill, StyleCoolLabel, StyleCoolIcon, style == IPhonePhotoStyle.Cool);

        ShowToast($"Phong cách: {GetStyleName(style)}");
    }

    private static void UpdateStylePill(Border pill, Label label, Microsoft.Maui.Controls.Shapes.Path? icon, bool isSelected)
    {
        pill.BackgroundColor = isSelected ? Color.FromArgb("#FFCC00") : Color.FromArgb("#26FFFFFF");
        label.TextColor = isSelected ? Colors.Black : Color.FromArgb("#E0FFFFFF");
        if (icon != null)
        {
            icon.Fill = isSelected ? Colors.Black : Color.FromArgb("#E0FFFFFF");
        }
        if (isSelected)
        {
            pill.Scale = 0.88;
            _ = pill.ScaleTo(1.06, 130, Easing.CubicOut).ContinueWith(_ =>
                MainThread.BeginInvokeOnMainThread(async () => await pill.ScaleTo(1.0, 100, Easing.SpringOut)));
        }
    }

    private static string GetStyleName(IPhonePhotoStyle style) => style switch
    {
        IPhonePhotoStyle.Standard => "Tiêu chuẩn",
        IPhonePhotoStyle.Warm => "Ấm áp",
        IPhonePhotoStyle.Vibrant => "Rực rỡ",
        IPhonePhotoStyle.RichContrast => "Tương phản phong phú",
        IPhonePhotoStyle.Cool => "Mát dịu",
        IPhonePhotoStyle.RawOriginal => "Ảnh gốc RAW",
        _ => "Tiêu chuẩn"
    };

    // =========================================================================
    // HỆ THỐNG THƯ VIỆN & TRỢ LÝ QUANG HỌC PHOTONIC AI STUDIO (RAW • PRO)
    // =========================================================================
    private byte[]? _currentOriginalBytes;
    private byte[]? _currentAiEditedBytes;
    private bool _isAiStudioOpen = false;
    private bool _isAiProcessing = false;
    private bool _isMultiSelectMode = false;
    private string _currentSortMode = "Newest"; // Newest | Oldest | Size
    private List<PhotoGalleryItemModel> _galleryViewModels = new();

    private async void OnThumbnailClicked(object? sender, EventArgs e)
    {
        await OpenGalleryAsync(0);
    }

    private async Task OpenGalleryAsync(int targetIndex = 0)
    {
        try
        {
            if (_storageService != null)
            {
                var photos = await _storageService.GetAllPhotosAsync();
                _allPhotos = photos ?? new List<PhotoItem>();
            }

            if (_allPhotos.Count == 0)
            {
                if (_lastPhotoBytes != null && _lastPhotoBytes.Length > 0)
                {
                    _currentOriginalBytes = _lastPhotoBytes;
                    _currentAiEditedBytes = null;
                    _isAiStudioOpen = false;
                    AiStudioPanel.IsVisible = false;
                    AiActionControlsBar.IsVisible = false;

                    ModalPreviewImage.Source = ImageSource.FromStream(() => new MemoryStream(_lastPhotoBytes));
                    ModalFileNameLabel.Text = _lastFileName;
                    ModalSpecsLabel.Text = $"Dung lượng: {_lastFileSizeString}";
                    ModalBadgeLabel.Text = _lastCaptureBadge;
                    ModalIndexLabel.Text = "1 / 1";
                    BtnPrevPhoto.Opacity = 0.3;
                    BtnNextPhoto.Opacity = 0.3;

                    SinglePhotoView.IsVisible = true;
                    GalleryGridView.IsVisible = false;
                    PhotoPreviewModal.Opacity = 0;
                    PhotoPreviewModal.IsVisible = true;
                    await PhotoPreviewModal.FadeTo(1, 180);
                    CameraUiRoot.IsVisible = false;
                    return;
                }

                ShowToast("Chưa có ảnh nào");
                return;
            }

            _currentPhotoIndex = Math.Clamp(targetIndex, 0, _allPhotos.Count - 1);
            DisplayPhotoAtIndex(_currentPhotoIndex);

            SinglePhotoView.IsVisible = true;
            GalleryGridView.IsVisible = false;
            PhotoPreviewModal.Opacity = 0;
            PhotoPreviewModal.IsVisible = true;
            await PhotoPreviewModal.FadeTo(1, 180);
            CameraUiRoot.IsVisible = false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OpenGalleryAsync] {ex.Message}");
            ShowToast("Không thể mở thư viện");
        }
    }

    private void DisplayPhotoAtIndex(int index)
    {
        if (index < 0 || index >= _allPhotos.Count) return;
        _currentPhotoIndex = index;
        var photo = _allPhotos[index];

        _currentAiEditedBytes = null;
        _isAiStudioOpen = false;
        _isAiProcessing = false;
        AiStudioPanel.IsVisible = false;
        AiActionControlsBar.IsVisible = false;
        MaskCanvasView.IsVisible = false;
        BrushToolBar.IsVisible = false;
        AiPromptDrawer.IsVisible = false;
        SinglePhotoBottomBar.IsVisible = true;

        ModalPreviewImage.Source = ImageSource.FromFile(photo.FilePath);
        ModalFileNameLabel.Text = photo.FileName;
        ModalSpecsLabel.Text = $"Dung lượng: {photo.FileSizeString}";
        ModalBadgeLabel.Text = photo.ModeTag;
        ModalIndexLabel.Text = $"{index + 1} / {_allPhotos.Count}";

        BtnPrevPhoto.Opacity = (index > 0) ? 1.0 : 0.25;
        BtnNextPhoto.Opacity = (index < _allPhotos.Count - 1) ? 1.0 : 0.25;

        // Tải trước byte gốc vào bộ nhớ đệm phục vụ AI Studio và Xoay/Lật ảnh
        _ = Task.Run(async () =>
        {
            if (_storageService != null)
            {
                _currentOriginalBytes = await _storageService.GetPhotoBytesAsync(photo.FilePath);
            }
        });
    }

    private void OnPhotoSwipedNext(object? sender, SwipedEventArgs e) => NavigatePhoto(1);
    private void OnPhotoSwipedPrev(object? sender, SwipedEventArgs e) => NavigatePhoto(-1);
    private void OnNextPhotoClicked(object? sender, EventArgs e) => NavigatePhoto(1);
    private void OnPrevPhotoClicked(object? sender, EventArgs e) => NavigatePhoto(-1);

    private async void NavigatePhoto(int delta)
    {
        int next = _currentPhotoIndex + delta;
        if (next >= 0 && next < _allPhotos.Count)
        {
            try { HapticFeedback.Default.Perform(HapticFeedbackType.Click); } catch { }
            double slideDir = delta > 0 ? -28 : 28;
            _ = ModalPreviewImage.FadeTo(0.35, 70, Easing.CubicIn);
            await ModalPreviewImage.TranslateTo(slideDir, 0, 70, Easing.CubicIn);

            DisplayPhotoAtIndex(next);

            ModalPreviewImage.TranslationX = -slideDir;
            _ = ModalPreviewImage.FadeTo(1.0, 140, Easing.CubicOut);
            await ModalPreviewImage.TranslateTo(0, 0, 140, Easing.CubicOut);
        }
    }

    // =========================================================================
    // AI STUDIO • TRỢ LÝ QUANG HỌC PHOTONIC AI AGENT
    // =========================================================================
    private async void OnToggleAiStudioClicked(object? sender, EventArgs e)
    {
        if (_isAiProcessing)
        {
            ShowToast("Photonic AI đang xử lý tác vụ...");
            return;
        }

        _isAiStudioOpen = !_isAiStudioOpen;
        AiStudioPanel.IsVisible = _isAiStudioOpen;

        if (_isAiStudioOpen)
        {
            AiDiagnosisLabel.Text = "Đang chẩn đoán bối cảnh ảnh...";
            byte[]? bytes = _currentOriginalBytes;
            if (bytes == null && _currentPhotoIndex >= 0 && _currentPhotoIndex < _allPhotos.Count && _storageService != null)
            {
                bytes = await _storageService.GetPhotoBytesAsync(_allPhotos[_currentPhotoIndex].FilePath);
                _currentOriginalBytes = bytes;
            }

            if (bytes != null && bytes.Length > 0)
            {
                string diagnosis = await PhotonicAiAgent.QuickDiagnoseJpegAsync(bytes);
                AiDiagnosisLabel.Text = diagnosis;
            }
            else
            {
                AiDiagnosisLabel.Text = "Sẵn sàng tối ưu ảnh";
            }
        }
    }

    private void OnAiAutoMasterTapped(object? sender, EventArgs e) => _ = ExecuteAiActionAsync(PhotonicAiAction.AutoMaster, "Tối ưu tự động");
    private void OnAiSubjectRelightTapped(object? sender, EventArgs e) => _ = ExecuteAiActionAsync(PhotonicAiAction.SubjectRelight, "Chiếu sáng chủ thể");
    private void OnAiSemanticBokehTapped(object? sender, EventArgs e) => _ = ExecuteAiActionAsync(PhotonicAiAction.SemanticBokeh, "Xóa phông nghệ thuật");
    private void OnAiSmartHdrSkyTapped(object? sender, EventArgs e) => _ = ExecuteAiActionAsync(PhotonicAiAction.SmartHdrSky, "Khôi phục mây trời HDR");
    private void OnAiCinematicLutTapped(object? sender, EventArgs e) => _ = ExecuteAiActionAsync(PhotonicAiAction.CinematicLut, "Màu điện ảnh");
    private void OnAiSuperSharpTapped(object? sender, EventArgs e) => _ = ExecuteAiActionAsync(PhotonicAiAction.SuperSharpDetail, "Siêu sắc nét vi mô");

    private async Task ExecuteAiActionAsync(PhotonicAiAction action, string actionName)
    {
        if (_isAiProcessing)
        {
            ShowToast("Đang có tác vụ AI đang thực thi...");
            return;
        }

        _isAiProcessing = true;
        try
        {
            byte[]? inputBytes = _currentOriginalBytes;
            if (inputBytes == null && _currentPhotoIndex >= 0 && _currentPhotoIndex < _allPhotos.Count && _storageService != null)
            {
                inputBytes = await _storageService.GetPhotoBytesAsync(_allPhotos[_currentPhotoIndex].FilePath);
                _currentOriginalBytes = inputBytes;
            }

            if (inputBytes == null || inputBytes.Length == 0)
            {
                ShowToast("Không có dữ liệu ảnh để xử lý");
                return;
            }

            AiDiagnosisLabel.Text = $"Đang thực thi: {actionName}...";
            var result = await PhotonicAiAgent.ProcessJpegAsync(inputBytes, action);
            if (result.OutputBytes != null && result.OutputBytes.Length > 0 && result.OutputBytes != inputBytes)
            {
                _currentAiEditedBytes = result.OutputBytes;
                ModalPreviewImage.Source = ImageSource.FromStream(() => new MemoryStream(_currentAiEditedBytes));
                AiDiagnosisLabel.Text = result.Diagnosis;
                AiActionControlsBar.IsVisible = true;
                ShowToast($"Đã áp dụng • {actionName}");
            }
            else
            {
                AiDiagnosisLabel.Text = result.Diagnosis;
                ShowToast(result.Diagnosis);
            }
        }
        catch (Exception ex)
        {
            AiDiagnosisLabel.Text = $"Lỗi: {ex.Message}";
            ShowToast("Xử lý AI không thành công");
        }
        finally
        {
            _isAiProcessing = false;
        }
    }

    private void OnCompareOriginalPressed(object? sender, EventArgs e)
    {
        if (_currentOriginalBytes != null && _currentOriginalBytes.Length > 0)
        {
            ModalPreviewImage.Source = ImageSource.FromStream(() => new MemoryStream(_currentOriginalBytes));
        }
    }

    private void OnCompareOriginalReleased(object? sender, EventArgs e)
    {
        if (_currentAiEditedBytes != null && _currentAiEditedBytes.Length > 0)
        {
            ModalPreviewImage.Source = ImageSource.FromStream(() => new MemoryStream(_currentAiEditedBytes));
        }
    }

    private async void OnSaveAiCopyClicked(object? sender, EventArgs e)
    {
        if (_currentAiEditedBytes == null || _storageService == null) return;

        try
        {
            string fileName = $"IMG_AI_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";
            await _storageService.SaveToGalleryAsync(_currentAiEditedBytes, fileName);

            var photos = await _storageService.GetAllPhotosAsync();
            _allPhotos = photos ?? new List<PhotoItem>();
            _currentPhotoIndex = 0;
            DisplayPhotoAtIndex(0);

            ShowToast("Đã lưu bản sao AI mới");
            AiActionControlsBar.IsVisible = false;
            UpdateCameraThumbnailWithLatest();
        }
        catch (Exception ex)
        {
            ShowToast($"Không thể lưu bản sao: {ex.Message}");
        }
    }

    private async void OnOverwriteAiClicked(object? sender, EventArgs e)
    {
        if (_currentAiEditedBytes == null || _currentPhotoIndex >= _allPhotos.Count || _storageService == null) return;

        try
        {
            var target = _allPhotos[_currentPhotoIndex];
            bool success = await _storageService.OverwritePhotoAsync(target.FilePath, _currentAiEditedBytes);
            if (success)
            {
                _currentOriginalBytes = _currentAiEditedBytes;
                _currentAiEditedBytes = null;
                AiActionControlsBar.IsVisible = false;

                double sizeKb = Math.Round(_currentOriginalBytes.Length / 1024.0, 1);
                string sizeStr = sizeKb > 1024 ? $"{Math.Round(sizeKb / 1024.0, 2)} MB" : $"{sizeKb} KB";
                ModalSpecsLabel.Text = $"Dung lượng: {sizeStr}";

                ShowToast("Đã ghi đè ảnh thành công");
                UpdateCameraThumbnailWithLatest();
            }
            else
            {
                ShowToast("Ghi đè không thành công");
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Lỗi ghi đè: {ex.Message}");
        }
    }

    // =========================================================================
    // CÁC CÔNG CỤ THAO TÁC ẢNH ĐƠN (XOAY, LẬT, CHIA SẺ, NHÂN BẢN, CHI TIẾT)
    // =========================================================================
    private async void OnRotatePhotoClicked(object? sender, EventArgs e)
    {
        if (_currentPhotoIndex < 0 || _currentPhotoIndex >= _allPhotos.Count || _storageService == null) return;

        try
        {
            var target = _allPhotos[_currentPhotoIndex];
            byte[]? bytes = _currentOriginalBytes ?? await _storageService.GetPhotoBytesAsync(target.FilePath);
            if (bytes == null || bytes.Length == 0) return;

            byte[] rotated = PhotonicAiAgent.RotateJpeg(bytes, 90);
            bool ok = await _storageService.OverwritePhotoAsync(target.FilePath, rotated);
            if (ok)
            {
                _currentOriginalBytes = rotated;
                ModalPreviewImage.Source = ImageSource.FromStream(() => new MemoryStream(rotated));
                ShowToast("Đã xoay ảnh 90°");
                UpdateCameraThumbnailWithLatest();
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Lỗi xoay ảnh: {ex.Message}");
        }
    }

    private async void OnFlipPhotoClicked(object? sender, EventArgs e)
    {
        if (_currentPhotoIndex < 0 || _currentPhotoIndex >= _allPhotos.Count || _storageService == null) return;

        try
        {
            var target = _allPhotos[_currentPhotoIndex];
            byte[]? bytes = _currentOriginalBytes ?? await _storageService.GetPhotoBytesAsync(target.FilePath);
            if (bytes == null || bytes.Length == 0) return;

            byte[] flipped = PhotonicAiAgent.FlipJpegHorizontal(bytes);
            bool ok = await _storageService.OverwritePhotoAsync(target.FilePath, flipped);
            if (ok)
            {
                _currentOriginalBytes = flipped;
                ModalPreviewImage.Source = ImageSource.FromStream(() => new MemoryStream(flipped));
                ShowToast("Đã lật ảnh ngang");
                UpdateCameraThumbnailWithLatest();
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Lỗi lật ảnh: {ex.Message}");
        }
    }

    private async void OnSharePhotoClicked(object? sender, EventArgs e)
    {
        if (_currentPhotoIndex < 0 || _currentPhotoIndex >= _allPhotos.Count || _storageService == null) return;
        var target = _allPhotos[_currentPhotoIndex];
        await _storageService.SharePhotoAsync(target.FilePath);
    }

    private async void OnDuplicatePhotoClicked(object? sender, EventArgs e)
    {
        if (_currentPhotoIndex < 0 || _currentPhotoIndex >= _allPhotos.Count || _storageService == null) return;

        try
        {
            var target = _allPhotos[_currentPhotoIndex];
            string dupPath = await _storageService.DuplicatePhotoAsync(target.FilePath);
            if (!string.IsNullOrEmpty(dupPath))
            {
                _allPhotos = await _storageService.GetAllPhotosAsync();
                ShowToast("Đã nhân bản ảnh thành công");
                UpdateCameraThumbnailWithLatest();
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Lỗi nhân bản: {ex.Message}");
        }
    }

    private void OnPhotoDetailsClicked(object? sender, EventArgs e)
    {
        if (_currentPhotoIndex < 0 || _currentPhotoIndex >= _allPhotos.Count) return;
        var target = _allPhotos[_currentPhotoIndex];

        DetailsFileNameLabel.Text = target.FileName;

        string resText = $"Dung lượng: {target.FileSizeString}";
        try
        {
#if ANDROID
            using var ex = new Android.Media.ExifInterface(target.FilePath);
            int w = ex.GetAttributeInt(Android.Media.ExifInterface.TagImageWidth, 0);
            int h = ex.GetAttributeInt(Android.Media.ExifInterface.TagImageLength, 0);
            if (w <= 0 || h <= 0)
            {
                var opt = new Android.Graphics.BitmapFactory.Options { InJustDecodeBounds = true };
                Android.Graphics.BitmapFactory.DecodeFile(target.FilePath, opt);
                w = opt.OutWidth;
                h = opt.OutHeight;
            }
            if (w > 0 && h > 0)
            {
                int mp = (int)Math.Round((w * (double)h) / 1_000_000.0);
                resText = $"Độ phân giải: {w} × {h} • {mp} MP\nDung lượng: {target.FileSizeString}";
            }
#endif
        }
        catch { }

        DetailsResolutionLabel.Text = resText;
        DetailsDateLabel.Text = target.FormattedDate;
        DetailsModeLabel.Text = target.ModeTag;
        DetailsPathLabel.Text = target.FilePath;

        PhotoDetailsModal.IsVisible = true;
    }

    private void OnCloseDetailsClicked(object? sender, EventArgs e)
    {
        PhotoDetailsModal.IsVisible = false;
    }

    private async void OnDeleteCurrentPhotoClicked(object? sender, EventArgs e)
    {
        if (_allPhotos.Count == 0 || _currentPhotoIndex < 0 || _currentPhotoIndex >= _allPhotos.Count) return;

        var target = _allPhotos[_currentPhotoIndex];
        bool confirm = await DisplayAlert("Xác nhận xóa", $"Bạn có chắc muốn xóa vĩnh viễn bức ảnh {target.FileName} không?", "Xóa", "Hủy");
        if (!confirm) return;

        bool deleted = false;
        if (_storageService != null)
        {
            deleted = await _storageService.DeletePhotoAsync(target.FilePath);
        }

        if (deleted)
        {
            _allPhotos.RemoveAt(_currentPhotoIndex);
            ApplyPhotoFilter(_currentFilter);

            ShowToast("Đã xóa ảnh thành công");

            if (_allPhotos.Count == 0)
            {
                OnCloseModalClicked(null, EventArgs.Empty);
                ThumbnailEmptyPath.IsVisible = true;
                LastCapturedPreview.IsVisible = false;
                _lastPhotoBytes = null;
            }
            else
            {
                if (_currentPhotoIndex >= _allPhotos.Count)
                {
                    _currentPhotoIndex = _allPhotos.Count - 1;
                }
                DisplayPhotoAtIndex(_currentPhotoIndex);
                UpdateCameraThumbnailWithLatest();
            }
        }
        else
        {
            ShowToast("Không thể xóa ảnh");
        }
    }

    private void UpdateCameraThumbnailWithLatest()
    {
        if (_allPhotos.Count > 0)
        {
            var newest = _allPhotos[0];
            LastCapturedPreview.Source = ImageSource.FromFile(newest.FilePath);
            LastCapturedPreview.IsVisible = true;
            ThumbnailEmptyPath.IsVisible = false;
        }
        else
        {
            LastCapturedPreview.IsVisible = false;
            ThumbnailEmptyPath.IsVisible = true;
        }
    }

    // =========================================================================
    // CHẾ ĐỘ XEM LƯỚI (GRID VIEW), SẮP XẾP & CHỌN NHIỀU (MULTI-SELECT)
    // =========================================================================
    private void OnSwitchToGridViewClicked(object? sender, EventArgs e)
    {
        MaskCanvasView.IsVisible = false;
        BrushToolBar.IsVisible = false;
        AiPromptDrawer.IsVisible = false;
        _maskDrawable.Clear();
        MaskCanvasView.Invalidate();
        SinglePhotoBottomBar.IsVisible = true;

        SinglePhotoView.IsVisible = false;
        GalleryGridView.IsVisible = true;
        ApplyPhotoFilter(_currentFilter);
    }

    private void OnSwitchToSingleViewClicked(object? sender, EventArgs e)
    {
        GalleryGridView.IsVisible = false;
        SinglePhotoView.IsVisible = true;
        DisplayPhotoAtIndex(_currentPhotoIndex);
    }

    private void OnToggleMultiSelectClicked(object? sender, EventArgs e)
    {
        _isMultiSelectMode = !_isMultiSelectMode;
        MultiSelectActionBar.IsVisible = _isMultiSelectMode;
        MultiSelectToggleLabel.Text = _isMultiSelectMode ? "Hủy" : "Chọn";

        foreach (var vm in _galleryViewModels)
        {
            vm.IsSelectionMode = _isMultiSelectMode;
            if (!_isMultiSelectMode) vm.IsSelected = false;
        }

        SelectedCountLabel.Text = "Đã chọn: 0";
    }

    private void OnSelectAllPhotosClicked(object? sender, EventArgs e)
    {
        bool anyUnselected = _galleryViewModels.Any(vm => !vm.IsSelected);
        foreach (var vm in _galleryViewModels)
        {
            vm.IsSelected = anyUnselected;
        }
        int selectedCount = _galleryViewModels.Count(vm => vm.IsSelected);
        SelectedCountLabel.Text = $"Đã chọn: {selectedCount}";
        SelectAllLabel.Text = anyUnselected ? "Bỏ chọn" : "Chọn tất cả";
    }

    private async void OnDeleteSelectedPhotosClicked(object? sender, EventArgs e)
    {
        var selected = _galleryViewModels.Where(vm => vm.IsSelected).ToList();
        if (selected.Count == 0)
        {
            ShowToast("Chưa chọn bức ảnh nào");
            return;
        }

        bool confirm = await DisplayAlert("Xác nhận xóa", $"Bạn có chắc muốn xóa vĩnh viễn {selected.Count} bức ảnh đã chọn không?", "Xóa", "Hủy");
        if (!confirm) return;

        int deletedCount = 0;
        if (_storageService != null)
        {
            foreach (var item in selected)
            {
                bool ok = await _storageService.DeletePhotoAsync(item.FilePath);
                if (ok) deletedCount++;
            }
        }

        _allPhotos = await _storageService!.GetAllPhotosAsync();
        _isMultiSelectMode = false;
        MultiSelectActionBar.IsVisible = false;
        MultiSelectToggleLabel.Text = "Chọn";

        ApplyPhotoFilter(_currentFilter);
        UpdateCameraThumbnailWithLatest();
        ShowToast($"Đã xóa {deletedCount} ảnh");
    }

    private async void OnShareSelectedPhotosClicked(object? sender, EventArgs e)
    {
        var selected = _galleryViewModels.Where(vm => vm.IsSelected).ToList();
        if (selected.Count == 0)
        {
            ShowToast("Chưa chọn bức ảnh nào");
            return;
        }

        if (_storageService != null)
        {
            foreach (var item in selected)
            {
                await _storageService.SharePhotoAsync(item.FilePath);
            }
        }
    }

    private void OnGridPhotoTapped(object? sender, EventArgs e)
    {
        if (sender is VisualElement ve && ve.BindingContext is PhotoGalleryItemModel tappedVm)
        {
            if (_isMultiSelectMode)
            {
                tappedVm.IsSelected = !tappedVm.IsSelected;
                int count = _galleryViewModels.Count(vm => vm.IsSelected);
                SelectedCountLabel.Text = $"Đã chọn: {count}";
            }
            else
            {
                int idx = _allPhotos.FindIndex(p => p.FilePath == tappedVm.FilePath);
                if (idx >= 0)
                {
                    _currentPhotoIndex = idx;
                }
                GalleryGridView.IsVisible = false;
                SinglePhotoView.IsVisible = true;
                DisplayPhotoAtIndex(_currentPhotoIndex);
            }
        }
    }

    private void OnFilterAllClicked(object? sender, EventArgs e) => ApplyPhotoFilter("All");
    private void OnFilterAiClicked(object? sender, EventArgs e) => ApplyPhotoFilter("AI");
    private void OnFilterPhotoClicked(object? sender, EventArgs e) => ApplyPhotoFilter("PHOTO");
    private void OnFilter48MpClicked(object? sender, EventArgs e) => ApplyPhotoFilter("48MP");
    private void OnFilterRawClicked(object? sender, EventArgs e) => ApplyPhotoFilter("RAW");

    private void OnSortNewestClicked(object? sender, EventArgs e) => ApplySortMode("Newest");
    private void OnSortOldestClicked(object? sender, EventArgs e) => ApplySortMode("Oldest");
    private void OnSortSizeClicked(object? sender, EventArgs e) => ApplySortMode("Size");

    private void ApplySortMode(string sortMode)
    {
        _currentSortMode = sortMode;

        SortNewestBorder.BackgroundColor = (sortMode == "Newest") ? Color.FromArgb("#333338") : Color.FromArgb("#1E1E22");
        SortNewestLabel.TextColor = (sortMode == "Newest") ? Color.FromArgb("#FFB800") : Color.FromArgb("#888888");
        SortNewestLabel.FontAttributes = (sortMode == "Newest") ? FontAttributes.Bold : FontAttributes.None;

        SortOldestBorder.BackgroundColor = (sortMode == "Oldest") ? Color.FromArgb("#333338") : Color.FromArgb("#1E1E22");
        SortOldestLabel.TextColor = (sortMode == "Oldest") ? Color.FromArgb("#FFB800") : Color.FromArgb("#888888");
        SortOldestLabel.FontAttributes = (sortMode == "Oldest") ? FontAttributes.Bold : FontAttributes.None;

        SortSizeBorder.BackgroundColor = (sortMode == "Size") ? Color.FromArgb("#333338") : Color.FromArgb("#1E1E22");
        SortSizeLabel.TextColor = (sortMode == "Size") ? Color.FromArgb("#FFB800") : Color.FromArgb("#888888");
        SortSizeLabel.FontAttributes = (sortMode == "Size") ? FontAttributes.Bold : FontAttributes.None;

        ApplyPhotoFilter(_currentFilter);
    }

    private void ApplyPhotoFilter(string filter)
    {
        _currentFilter = filter;

        FilterAllBorder.BackgroundColor = (filter == "All") ? Color.FromArgb("#FFCC00") : Color.FromArgb("#222222");
        FilterAllLabel.TextColor = (filter == "All") ? Colors.Black : Colors.White;
        FilterAllLabel.FontAttributes = (filter == "All") ? FontAttributes.Bold : FontAttributes.None;

        FilterAiBorder.BackgroundColor = (filter == "AI") ? Color.FromArgb("#FFCC00") : Color.FromArgb("#222222");
        FilterAiLabel.TextColor = (filter == "AI") ? Colors.Black : Colors.White;
        FilterAiLabel.FontAttributes = (filter == "AI") ? FontAttributes.Bold : FontAttributes.None;

        FilterPhotoBorder.BackgroundColor = (filter == "PHOTO") ? Color.FromArgb("#FFCC00") : Color.FromArgb("#222222");
        FilterPhotoLabel.TextColor = (filter == "PHOTO") ? Colors.Black : Colors.White;
        FilterPhotoLabel.FontAttributes = (filter == "PHOTO") ? FontAttributes.Bold : FontAttributes.None;

        Filter48MpBorder.BackgroundColor = (filter == "48MP") ? Color.FromArgb("#FFCC00") : Color.FromArgb("#222222");
        Filter48MpLabel.TextColor = (filter == "48MP") ? Colors.Black : Colors.White;
        Filter48MpLabel.FontAttributes = (filter == "48MP") ? FontAttributes.Bold : FontAttributes.None;

        FilterRawBorder.BackgroundColor = (filter == "RAW") ? Color.FromArgb("#FFCC00") : Color.FromArgb("#222222");
        FilterRawLabel.TextColor = (filter == "RAW") ? Colors.Black : Colors.White;
        FilterRawLabel.FontAttributes = (filter == "RAW") ? FontAttributes.Bold : FontAttributes.None;

        var sourceList = _allPhotos.ToList();

        // 1. Áp dụng Sắp xếp
        sourceList = _currentSortMode switch
        {
            "Oldest" => sourceList.OrderBy(p => p.CreatedAt).ToList(),
            "Size" => sourceList.OrderByDescending(p => p.FileSizeBytes).ToList(),
            _ => sourceList.OrderByDescending(p => p.CreatedAt).ToList() // Mặc định: Mới nhất
        };

        // 2. Áp dụng Lọc
        _filteredPhotos = filter switch
        {
            "AI" => sourceList.Where(p => p.ModeTag.Contains("AI", StringComparison.OrdinalIgnoreCase)).ToList(),
            "PHOTO" => sourceList.Where(p => p.ModeTag.Contains("PHOTO", StringComparison.OrdinalIgnoreCase)).ToList(),
            "48MP" => sourceList.Where(p => p.ModeTag.Contains("48 MP", StringComparison.OrdinalIgnoreCase) || p.FileName.Contains("48MP", StringComparison.OrdinalIgnoreCase)).ToList(),
            "RAW" => sourceList.Where(p => p.ModeTag.Contains("RAW", StringComparison.OrdinalIgnoreCase)).ToList(),
            _ => sourceList
        };

        _galleryViewModels = _filteredPhotos.Select(p => new PhotoGalleryItemModel(p)
        {
            IsSelectionMode = _isMultiSelectMode
        }).ToList();

        GalleryCollectionView.ItemsSource = _galleryViewModels;
        if (filter == "All")
        {
            GalleryStatsLabel.Text = $"{_allPhotos.Count} ảnh • {CalcTotalSize(_allPhotos)}";
        }
        else
        {
            GalleryStatsLabel.Text = $"{_filteredPhotos.Count}/{_allPhotos.Count} ảnh • {CalcTotalSize(_filteredPhotos)}";
        }
    }

    private static string CalcTotalSize(List<PhotoItem> photos)
    {
        long totalBytes = photos.Sum(p => p.FileSizeBytes);
        double mb = Math.Round(totalBytes / (1024.0 * 1024.0), 1);
        return $"{mb} MB";
    }

    private void OnCloseModalClicked(object? sender, EventArgs e)
    {
        _isAiProcessing = false;
        CameraUiRoot.IsVisible = true;
        MaskCanvasView.IsVisible = false;
        BrushToolBar.IsVisible = false;
        AiPromptDrawer.IsVisible = false;
        _maskDrawable.Clear();
        MaskCanvasView.Invalidate();
        SinglePhotoBottomBar.IsVisible = true;

        _ = PhotoPreviewModal.FadeTo(0, 150).ContinueWith(_ =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                PhotoPreviewModal.IsVisible = false;
            });
        });
    }

    private static readonly Microsoft.Maui.Controls.Shapes.PathGeometryConverter _geoConverter = new();

    private static Microsoft.Maui.Controls.Shapes.Geometry GetIconGeometry(string resourceKey)
    {
        if (Application.Current?.Resources.TryGetValue(resourceKey, out var val) == true && val is string str)
        {
            return (Microsoft.Maui.Controls.Shapes.Geometry)_geoConverter.ConvertFromInvariantString(str)!;
        }
        return new Microsoft.Maui.Controls.Shapes.PathGeometry();
    }

    // ==========================================
    // CỌ TÔ VẬT THỂ & NHẬP PROMPT LỆNH AI
    // ==========================================

    private void OnStartMaskBrushTapped(object? sender, EventArgs e)
    {
        AiStudioPanel.IsVisible = false;
        SinglePhotoBottomBar.IsVisible = false;
        _maskDrawable.Clear();
        MaskCanvasView.Invalidate();
        MaskCanvasView.IsVisible = true;
        BrushToolBar.IsVisible = true;
        AiPromptDrawer.IsVisible = true;
        AiPromptEntry.Text = "Xóa vật thể này đi";
        ShowToast("Dùng ngón tay tô lên vật thể cần xóa hoặc sửa");
    }

    private void OnCloseBrushClicked(object? sender, EventArgs e)
    {
        MaskCanvasView.IsVisible = false;
        BrushToolBar.IsVisible = false;
        AiPromptDrawer.IsVisible = false;
        _maskDrawable.Clear();
        MaskCanvasView.Invalidate();
        AiStudioPanel.IsVisible = true;
        SinglePhotoBottomBar.IsVisible = true;
    }

    private void OnBrushSizeSmallClicked(object? sender, EventArgs e)
    {
        _maskDrawable.CurrentBrushRadius = 14f;
        BrushSizeSmallBorder.BackgroundColor = Color.FromArgb("#FFCC00");
        BrushSizeMedBorder.BackgroundColor = Color.FromArgb("#22FFFFFF");
        BrushSizeLargeBorder.BackgroundColor = Color.FromArgb("#22FFFFFF");
    }

    private void OnBrushSizeMedClicked(object? sender, EventArgs e)
    {
        _maskDrawable.CurrentBrushRadius = 26f;
        BrushSizeSmallBorder.BackgroundColor = Color.FromArgb("#22FFFFFF");
        BrushSizeMedBorder.BackgroundColor = Color.FromArgb("#FFCC00");
        BrushSizeLargeBorder.BackgroundColor = Color.FromArgb("#22FFFFFF");
    }

    private void OnBrushSizeLargeClicked(object? sender, EventArgs e)
    {
        _maskDrawable.CurrentBrushRadius = 44f;
        BrushSizeSmallBorder.BackgroundColor = Color.FromArgb("#22FFFFFF");
        BrushSizeMedBorder.BackgroundColor = Color.FromArgb("#22FFFFFF");
        BrushSizeLargeBorder.BackgroundColor = Color.FromArgb("#FFCC00");
    }

    private void OnBrushUndoClicked(object? sender, EventArgs e)
    {
        _maskDrawable.Undo();
        MaskCanvasView.Invalidate();
    }

    private void OnBrushClearClicked(object? sender, EventArgs e)
    {
        _maskDrawable.Clear();
        MaskCanvasView.Invalidate();
    }

    private void OnMaskCanvasStartInteraction(object? sender, TouchEventArgs e)
    {
        if (e.Touches == null || e.Touches.Length == 0) return;
        _maskDrawable.StartStroke((float)e.Touches[0].X, (float)e.Touches[0].Y);
        MaskCanvasView.Invalidate();
    }

    private void OnMaskCanvasDragInteraction(object? sender, TouchEventArgs e)
    {
        if (e.Touches == null || e.Touches.Length == 0) return;
        _maskDrawable.AddPoint((float)e.Touches[0].X, (float)e.Touches[0].Y);
        MaskCanvasView.Invalidate();
    }

    private void OnMaskCanvasEndInteraction(object? sender, TouchEventArgs e)
    {
        _maskDrawable.EndStroke();
        MaskCanvasView.Invalidate();
    }

    private void OnQuickChipEraseTapped(object? sender, EventArgs e) => AiPromptEntry.Text = "Xóa vật thể này đi";
    private void OnQuickChipBrightenTapped(object? sender, EventArgs e) => AiPromptEntry.Text = "Làm sáng vùng này";
    private void OnQuickChipBlurTapped(object? sender, EventArgs e) => AiPromptEntry.Text = "Làm mờ xóa phông";
    private void OnQuickChipColorTapped(object? sender, EventArgs e) => AiPromptEntry.Text = "Đổi màu nghệ thuật";
    private void OnQuickChipSharpenTapped(object? sender, EventArgs e) => AiPromptEntry.Text = "Tăng nét chi tiết";

    private void OnPromptEntryCompleted(object? sender, EventArgs e)
    {
        OnExecutePromptTapped(sender, e);
    }

    private async void OnExecutePromptTapped(object? sender, EventArgs e)
    {
        if (_isAiProcessing)
        {
            ShowToast("Đang có tác vụ AI đang thực thi...");
            return;
        }

        byte[]? inputBytes = _currentAiEditedBytes ?? _currentOriginalBytes;
        if (inputBytes == null && _currentPhotoIndex >= 0 && _currentPhotoIndex < _allPhotos.Count && _storageService != null)
        {
            inputBytes = await _storageService.GetPhotoBytesAsync(_allPhotos[_currentPhotoIndex].FilePath);
            _currentOriginalBytes = inputBytes;
        }

        if (inputBytes == null || inputBytes.Length == 0)
        {
            ShowToast("Chưa có ảnh để xử lý");
            return;
        }

        if (!_maskDrawable.HasStrokes)
        {
            ShowToast("Vui lòng dùng ngón tay tô lên vật thể");
            return;
        }

        string prompt = AiPromptEntry.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(prompt))
        {
            prompt = "Xóa vật thể này đi";
        }

        _isAiProcessing = true;
        ShowToast("Photonic AI đang xử lý lệnh...");

        try
        {
            var canvasW = (float)MaskCanvasView.Width;
            var canvasH = (float)MaskCanvasView.Height;
            if (canvasW <= 10 || canvasH <= 10)
            {
                canvasW = (float)ModalPreviewImage.Width;
                canvasH = (float)ModalPreviewImage.Height;
            }

            var (processedBytes, statusMsg) = await Task.Run(async () =>
            {
#if ANDROID
                var (workW, workH) = PhotonicAiAgent.GetWorkingDimensions(inputBytes);

                float imgAspect = (float)workW / workH;
                float viewAspect = (canvasH > 0) ? (canvasW / canvasH) : 1f;

                float dispW, dispH, dispX, dispY;
                if (viewAspect > imgAspect)
                {
                    dispH = canvasH;
                    dispW = canvasH * imgAspect;
                    dispX = (canvasW - dispW) * 0.5f;
                    dispY = 0;
                }
                else
                {
                    dispW = canvasW;
                    dispH = (imgAspect > 0) ? (canvasW / imgAspect) : canvasH;
                    dispX = 0;
                    dispY = (canvasH - dispH) * 0.5f;
                }

                var dispRect = new Microsoft.Maui.Graphics.RectF(dispX, dispY, dispW, dispH);
                byte[] binaryMask = _maskDrawable.GenerateBinaryMask(workW, workH, dispRect);

                return await PhotonicAiAgent.ExecutePromptOnMaskAsync(
                    inputBytes,
                    binaryMask,
                    workW,
                    workH,
                    prompt);
#else
                return (inputBytes, "Chỉ hỗ trợ trên Android");
#endif
            });

            if (processedBytes != null && processedBytes.Length > 0 && processedBytes != inputBytes)
            {
                _currentAiEditedBytes = processedBytes;

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    ModalPreviewImage.Source = ImageSource.FromStream(() => new MemoryStream(_currentAiEditedBytes));
                    _maskDrawable.Clear();
                    MaskCanvasView.Invalidate();
                    MaskCanvasView.IsVisible = false;
                    BrushToolBar.IsVisible = false;
                    AiPromptDrawer.IsVisible = false;

                    AiStudioPanel.IsVisible = true;
                    AiActionControlsBar.IsVisible = true;
                    SinglePhotoBottomBar.IsVisible = true;

                    ShowToast(statusMsg);
                });
            }
            else
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    ShowToast(statusMsg);
                });
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Lỗi AI: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ExecutePrompt] {ex}");
        }
        finally
        {
            _isAiProcessing = false;
        }
    }

    public void TestStartBrush()
    {
        MainThread.BeginInvokeOnMainThread(() => OnStartMaskBrushTapped(null, EventArgs.Empty));
    }

    public void TestExecutePrompt(string prompt = "Xóa vật thể này đi")
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!_maskDrawable.HasStrokes)
            {
                float midX = (float)(MaskCanvasView.Width * 0.5);
                float midY = (float)(MaskCanvasView.Height * 0.5);
                if (midX <= 0) { midX = 200; midY = 300; }
                _maskDrawable.StartStroke(midX - 30, midY - 30);
                _maskDrawable.AddPoint(midX + 30, midY + 30);
                _maskDrawable.EndStroke();
            }
            AiPromptEntry.Text = prompt;
            OnExecutePromptTapped(null, EventArgs.Empty);
        });
    }
}

/// <summary>
/// ViewModel bọc mục ảnh trong thư viện phục vụ chọn nhiều (Multi-Select)
/// </summary>
public class PhotoGalleryItemModel : System.ComponentModel.INotifyPropertyChanged
{
    public PhotoItem Photo { get; }
    public string FilePath => Photo.FilePath;
    public string FileName => Photo.FileName;
    public string FileSizeString => Photo.FileSizeString;
    public long FileSizeBytes => Photo.FileSizeBytes;
    public DateTime CreatedAt => Photo.CreatedAt;
    public string ModeTag => Photo.ModeTag;
    public string FormattedDate => Photo.FormattedDate;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
                OnPropertyChanged(nameof(SelectionBgColor));
                OnPropertyChanged(nameof(SelectionBorderColor));
                OnPropertyChanged(nameof(SelectionCheckColor));
            }
        }
    }

    private bool _isSelectionMode;
    public bool IsSelectionMode
    {
        get => _isSelectionMode;
        set
        {
            if (_isSelectionMode != value)
            {
                _isSelectionMode = value;
                OnPropertyChanged(nameof(IsSelectionMode));
            }
        }
    }

    public Color SelectionBgColor => IsSelected ? Color.FromArgb("#FFCC00") : Color.FromArgb("#66000000");
    public Color SelectionBorderColor => IsSelected ? Color.FromArgb("#FFCC00") : Color.FromArgb("#88FFFFFF");
    public Color SelectionCheckColor => IsSelected ? Colors.Black : Colors.Transparent;

    public PhotoGalleryItemModel(PhotoItem photo)
    {
        Photo = photo;
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string propName) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propName));
}
