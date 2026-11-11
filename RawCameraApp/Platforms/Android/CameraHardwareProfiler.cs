#if ANDROID
using Android.Content;
using Android.Hardware.Camera2;
using Android.OS;
using RawCameraApp.Services.Imaging;

namespace RawCameraApp.Platforms.Android;

/// <summary>
/// Dịch vụ quét và nhận diện cấu trúc ống kính quang học Camera2 đa thiết bị (Universal Multi-Lens Profiler).
/// Tương thích với tất cả các dòng máy: Xiaomi/POCO/Redmi, Samsung Galaxy, Google Pixel, Oppo/Realme, Vivo, Sony...
/// </summary>
public static class CameraHardwareProfiler
{
    private const float FullFrameDiagonalMm = 43.2666f; // sqrt(36^2 + 24^2)

    public static OemCameraVendor DetectVendor()
    {
        string mfg = (Build.Manufacturer ?? string.Empty).ToLowerInvariant();
        string brand = (Build.Brand ?? string.Empty).ToLowerInvariant();

        if (mfg.Contains("xiaomi") || mfg.Contains("poco") || mfg.Contains("redmi") || brand.Contains("poco") || brand.Contains("redmi"))
            return OemCameraVendor.XiaomiPocoRedmi;
        if (mfg.Contains("samsung"))
            return OemCameraVendor.Samsung;
        if (mfg.Contains("google"))
            return OemCameraVendor.GooglePixel;
        if (mfg.Contains("oppo") || mfg.Contains("oneplus") || mfg.Contains("realme"))
            return OemCameraVendor.OppoOnePlusRealme;
        if (mfg.Contains("vivo") || mfg.Contains("iqoo"))
            return OemCameraVendor.VivoIqoo;
        if (mfg.Contains("sony"))
            return OemCameraVendor.Sony;

        return OemCameraVendor.Generic;
    }

    public static (List<CameraLensProfile> BackLenses, List<CameraLensProfile> FrontLenses) DiscoverLenses(
        IList<Camera.MAUI.CameraInfo>? mauiCameras)
    {
        var backLenses = new List<CameraLensProfile>();
        var frontLenses = new List<CameraLensProfile>();

        if (mauiCameras == null || mauiCameras.Count == 0)
        {
            return (backLenses, frontLenses);
        }

        var context = global::Android.App.Application.Context;
        var cameraManager = context.GetSystemService(Context.CameraService) as CameraManager;
        var vendor = DetectVendor();
        string deviceModel = $"{Build.Manufacturer} {Build.Model}".Trim();

        if (cameraManager == null)
        {
            // Fallback an toàn nếu không lấy được CameraManager
            CreateFallbackProfiles(mauiCameras, vendor, deviceModel, backLenses, frontLenses);
            return (backLenses, frontLenses);
        }

        // 1. Thu thập danh sách Camera ID từ nhiều nguồn để đảm bảo quét được toàn bộ ống kính
        var candidateIds = new List<string>();

        if (mauiCameras != null)
        {
            foreach (var cam in mauiCameras)
            {
                if (!string.IsNullOrEmpty(cam.DeviceId) && !candidateIds.Contains(cam.DeviceId))
                    candidateIds.Add(cam.DeviceId);
            }
        }

        try
        {
            var sysIds = cameraManager.GetCameraIdList();
            if (sysIds != null)
            {
                foreach (var sid in sysIds)
                {
                    if (!string.IsNullOrEmpty(sid) && !candidateIds.Contains(sid))
                        candidateIds.Add(sid);
                }
            }
        }
        catch { }

        // Mở rộng tìm kiếm các camera vật lý OEM (Xiaomi, POCO, Samsung, Sony...)
        for (int i = 0; i <= 8; i++)
        {
            string idStr = i.ToString();
            if (!candidateIds.Contains(idStr))
                candidateIds.Add(idStr);
        }

        var discoveredRaw = new List<RawLensCandidate>();

        foreach (var camId in candidateIds)
        {
            try
            {
                var chars = cameraManager.GetCameraCharacteristics(camId);
                if (chars == null) continue;

                // Kiểm tra BackwardCompatible
                var caps = (int[]?)chars.Get(CameraCharacteristics.RequestAvailableCapabilities);
                if (caps != null)
                {
                    bool isBackwardComp = false;
                    foreach (int b in caps)
                    {
                        if (b == (int)RequestAvailableCapabilities.BackwardCompatible)
                        {
                            isBackwardComp = true;
                            break;
                        }
                    }
                    if (!isBackwardComp) continue;
                }

                int facing = 0; // 0 = Back, 1 = Front
                if (chars.Get(CameraCharacteristics.LensFacing) is Java.Lang.Integer lf)
                {
                    facing = lf.IntValue() == (int)LensFacing.Front ? 1 : 0;
                }
                else
                {
                    var existingMaui = mauiCameras?.FirstOrDefault(c => c.DeviceId == camId);
                    if (existingMaui != null)
                    {
                        facing = (existingMaui.Position == Camera.MAUI.CameraPosition.Front) ? 1 : 0;
                    }
                }

                // Tiêu cự
                float focalLength = 4.5f;
                var fls = (float[]?)chars.Get(CameraCharacteristics.LensInfoAvailableFocalLengths);
                if (fls != null && fls.Length > 0)
                {
                    focalLength = fls[0];
                }

                // Khẩu độ
                float aperture = 2.0f;
                var aps = (float[]?)chars.Get(CameraCharacteristics.LensInfoAvailableApertures);
                if (aps != null && aps.Length > 0)
                {
                    aperture = aps[0];
                }

                // Kích thước cảm biến vật lý (mm)
                float sensorW = 6.0f, sensorH = 4.5f;
                if (chars.Get(CameraCharacteristics.SensorInfoPhysicalSize) is global::Android.Util.SizeF ps)
                {
                    sensorW = ps.Width;
                    sensorH = ps.Height;
                }

                // Độ phân giải cảm biến (pixel)
                int pixelW = 4000, pixelH = 3000;
                if (chars.Get(CameraCharacteristics.SensorInfoPixelArraySize) is global::Android.Util.Size pas)
                {
                    pixelW = pas.Width;
                    pixelH = pas.Height;
                }

                // Đèn Flash
                bool hasFlash = false;
                if (chars.Get(CameraCharacteristics.FlashInfoAvailable) is Java.Lang.Boolean fb)
                {
                    hasFlash = fb.BooleanValue();
                }

                // Lấy nét tự động (AF)
                bool hasAf = false;
                if (chars.Get(CameraCharacteristics.LensInfoMinimumFocusDistance) is Java.Lang.Float mfd)
                {
                    hasAf = mfd.FloatValue() > 0.001f;
                }

                double mp = (pixelW * (double)pixelH) / 1_000_000.0;

                // LỌC BỎ CẢM BIẾN DEPTH / BOKEH:
                // Camera sau có độ phân giải <= 2.2MP và không có đèn flash (như Camera ID 2 trên Poco/Xiaomi)
                if (facing == 0 && mp <= 2.2 && !hasFlash && !hasAf)
                {
                    continue;
                }

                // LỌC BỎ CAMERA ẢO / TRÙNG LẶP (Duplicate Virtual Multi-Camera):
                // Nếu cùng Facing, cùng tiêu cự xấp xỉ, cùng kích thước cảm biến và cùng độ phân giải
                bool isDuplicate = discoveredRaw.Any(r =>
                    r.Facing == facing &&
                    Math.Abs(r.FocalLength - focalLength) < 0.05f &&
                    Math.Abs(r.SensorW - sensorW) < 0.05f &&
                    Math.Abs(r.PixelW - pixelW) < 20);

                if (isDuplicate)
                {
                    continue;
                }

                // Kiểm tra ControlZoomRatioRange (Android 11+ / API 30+)
                float minZoomRatio = 1.0f;
                float maxZoomRatio = 1.0f;
                if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
                {
                    try
                    {
                        if (chars.Get(CameraCharacteristics.ControlZoomRatioRange) is global::Android.Util.Range zr)
                        {
                            if (zr.Lower is Java.Lang.Float zLower) minZoomRatio = zLower.FloatValue();
                            if (zr.Upper is Java.Lang.Float zUpper) maxZoomRatio = zUpper.FloatValue();
                        }
                    }
                    catch { }
                }

                // Tìm hoặc tạo CameraInfo cho Camera.MAUI
                var mauiCam = mauiCameras?.FirstOrDefault(c => c.DeviceId == camId);
                if (mauiCam == null)
                {
                    mauiCam = CreateMauiCameraInfo(
                        camId,
                        (facing == 1) ? "Front Camera" : $"Camera {camId}",
                        (facing == 1) ? Camera.MAUI.CameraPosition.Front : Camera.MAUI.CameraPosition.Back,
                        hasFlash,
                        chars
                    );
                    mauiCameras?.Add(mauiCam);
                }

                global::Android.Util.Log.Debug("app_process64", $"[CameraHardwareProfiler] Discovered Lens: Cam {camId}, Facing={(facing == 0 ? "Back" : "Front")}, Focal={focalLength}mm, Sensor={sensorW:F1}x{sensorH:F1}mm, Flash={hasFlash}, AF={hasAf}, MP={mp:F1}");

                discoveredRaw.Add(new RawLensCandidate(
                    mauiCam,
                    camId,
                    facing,
                    focalLength,
                    aperture,
                    sensorW,
                    sensorH,
                    pixelW,
                    pixelH,
                    hasFlash,
                    hasAf,
                    minZoomRatio,
                    maxZoomRatio,
                    chars
                ));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CameraHardwareProfiler] Error probing cam {camId}: {ex.Message}");
            }
        }

        // 2. Phân loại và tạo danh sách Camera Sau (Back Lenses)
        var backCandidates = discoveredRaw.Where(c => c.Facing == 0).ToList();
        if (backCandidates.Count > 0)
        {
            // Xác định Camera Chính (Wide Main): Camera có diện tích cảm biến lớn nhất trong nhóm tiêu cự chuẩn 2.8mm - 6.5mm
            var mainCandidate = backCandidates
                .OrderByDescending(c => (c.SensorW * c.SensorH))
                .FirstOrDefault();

            if (mainCandidate == null) mainCandidate = backCandidates[0];

            float mainEquivFocal = Calc35mmEquivalent(mainCandidate.FocalLength, mainCandidate.SensorW, mainCandidate.SensorH);

            // A. Camera Góc Siêu Rộng (Ultra-Wide)
            RawLensCandidate? ultraWideCandidate = null;
            foreach (var cand in backCandidates)
            {
                if (cand == mainCandidate) continue;
                float eqFocal = Calc35mmEquivalent(cand.FocalLength, cand.SensorW, cand.SensorH);
                if (eqFocal < 21.0f || cand.FocalLength < 2.5f)
                {
                    ultraWideCandidate = cand;
                    break;
                }
            }

            if (ultraWideCandidate != null)
            {
                float uwEquiv = Calc35mmEquivalent(ultraWideCandidate.FocalLength, ultraWideCandidate.SensorW, ultraWideCandidate.SensorH);
                float opticalRatio = (mainEquivFocal > 1.0f) ? (float)Math.Round((uwEquiv / mainEquivFocal) * 10f) / 10f : 0.6f;
                if (opticalRatio < 0.4f || opticalRatio > 0.8f) opticalRatio = 0.6f;

                string badge = $"{opticalRatio:0.#}x";
                backLenses.Add(CreateProfile(
                    ultraWideCandidate,
                    CameraLensType.UltraWide,
                    badge,
                    $"Góc siêu rộng {badge}",
                    opticalRatio,
                    1.0f,
                    false,
                    false,
                    vendor,
                    deviceModel
                ));
            }
            else if (mainCandidate.MinZoomRatio < 0.85f && Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                // Máy dùng Logical Multi-Camera (Samsung / Google Pixel):
                // Có thể mở góc rộng bằng ControlZoomRatio < 1.0
                float logicalZoom = mainCandidate.MinZoomRatio <= 0.55f ? 0.5f : 0.6f;
                string badge = $"{logicalZoom:0.#}x";
                backLenses.Add(CreateLogicalProfile(
                    mainCandidate,
                    CameraLensType.UltraWide,
                    badge,
                    $"Góc siêu rộng {badge}",
                    logicalZoom,
                    logicalZoom,
                    true,
                    false,
                    vendor,
                    deviceModel
                ));
            }

            // B. Camera Chính (1x)
            backLenses.Add(CreateProfile(
                mainCandidate,
                CameraLensType.WideMain,
                "1x",
                "Camera chính 1x",
                1.0f,
                1.0f,
                false,
                false,
                vendor,
                deviceModel
            ));

            // C. Camera Telephoto hoặc 2x Crop
            RawLensCandidate? teleCandidate = null;
            foreach (var cand in backCandidates)
            {
                if (cand == mainCandidate || cand == ultraWideCandidate) continue;
                float eqFocal = Calc35mmEquivalent(cand.FocalLength, cand.SensorW, cand.SensorH);
                if (eqFocal >= 42.0f || cand.FocalLength >= 7.0f)
                {
                    teleCandidate = cand;
                    break;
                }
            }

            if (teleCandidate != null)
            {
                float teleEquiv = Calc35mmEquivalent(teleCandidate.FocalLength, teleCandidate.SensorW, teleCandidate.SensorH);
                float teleRatio = (mainEquivFocal > 1.0f) ? (float)Math.Round((teleEquiv / mainEquivFocal) * 10f) / 10f : 2.0f;
                string teleBadge = $"{teleRatio:0.#}x";

                backLenses.Add(CreateProfile(
                    teleCandidate,
                    CameraLensType.Telephoto,
                    teleBadge,
                    $"Ống kính Tele {teleBadge}",
                    teleRatio,
                    1.0f,
                    false,
                    false,
                    vendor,
                    deviceModel
                ));
            }
            else
            {
                // Tự động tạo chế độ Tele 2x In-Sensor Crop trên cảm biến chính
                backLenses.Add(CreateProfile(
                    mainCandidate,
                    CameraLensType.Telephoto,
                    "2x",
                    "Tele 2x Cắt cảm biến",
                    2.0f,
                    2.0f,
                    false,
                    true,
                    vendor,
                    deviceModel
                ));
            }

            // D. Camera Macro (Cận cảnh)
            RawLensCandidate? macroCandidate = null;
            foreach (var cand in backCandidates)
            {
                if (cand == mainCandidate || cand == ultraWideCandidate || cand == teleCandidate) continue;
                double mp = (cand.PixelW * (double)cand.PixelH) / 1_000_000.0;
                // Macro thường có MP <= 5MP, f/2.4 và có đèn flash hỗ trợ
                if (mp <= 5.5)
                {
                    macroCandidate = cand;
                    break;
                }
            }

            if (macroCandidate != null)
            {
                backLenses.Add(CreateProfile(
                    macroCandidate,
                    CameraLensType.Macro,
                    "Macro",
                    "Cận cảnh Macro",
                    1.0f,
                    1.0f,
                    false,
                    false,
                    vendor,
                    deviceModel
                ));
            }
        }

        // 3. Phân loại và tạo danh sách Camera Trước (Front Lenses)
        var frontCandidates = discoveredRaw.Where(c => c.Facing == 1).ToList();
        if (frontCandidates.Count > 0)
        {
            var mainFront = frontCandidates[0];
            frontLenses.Add(CreateProfile(
                mainFront,
                CameraLensType.FrontSelfie,
                "1x",
                "Camera trước Selfie",
                1.0f,
                1.0f,
                false,
                false,
                vendor,
                deviceModel
            ));

            // Nếu máy có 2 camera trước (ví dụ có thêm cam góc rộng)
            if (frontCandidates.Count > 1)
            {
                var uwFront = frontCandidates[1];
                frontLenses.Add(CreateProfile(
                    uwFront,
                    CameraLensType.FrontUltraWide,
                    "0.8x",
                    "Selfie góc rộng",
                    0.8f,
                    1.0f,
                    false,
                    false,
                    vendor,
                    deviceModel
                ));
            }
        }

        // Sắp xếp lại thứ tự nút cho hợp lý:
        // Mặt sau: UltraWide (0.6x) -> WideMain (1x) -> Tele (2x/3x) -> Macro
        backLenses.Sort((a, b) =>
        {
            int GetOrder(CameraLensProfile p) => p.LensType switch
            {
                CameraLensType.UltraWide => 0,
                CameraLensType.WideMain => 1,
                CameraLensType.Telephoto => 2,
                CameraLensType.Macro => 3,
                _ => 4
            };
            return GetOrder(a).CompareTo(GetOrder(b));
        });

        return (backLenses, frontLenses);
    }

    private static void CreateFallbackProfiles(
        IList<Camera.MAUI.CameraInfo> mauiCameras,
        OemCameraVendor vendor,
        string deviceModel,
        List<CameraLensProfile> backLenses,
        List<CameraLensProfile> frontLenses)
    {
        var backCam = mauiCameras.FirstOrDefault(c => c.Position == Camera.MAUI.CameraPosition.Back) ?? mauiCameras.FirstOrDefault();
        var frontCam = mauiCameras.FirstOrDefault(c => c.Position == Camera.MAUI.CameraPosition.Front);

        if (backCam != null)
        {
            backLenses.Add(new CameraLensProfile(
                DeviceId: backCam.DeviceId,
                MauiCamera: backCam,
                LensType: CameraLensType.WideMain,
                ShortBadge: "1x",
                DisplayName: "Camera chính 1x",
                SpecSummary: "26mm f/1.8",
                OpticalZoomRatio: 1.0f,
                TargetHardwareZoom: 1.0f,
                UsesLogicalZoomRatio: false,
                IsSensorCropZoom: false,
                FocalLengthMm: 4.5f,
                FocalLength35mmEquivalent: 26,
                Aperture: 1.8f,
                SensorWidthMm: 6.4f,
                SensorHeightMm: 4.8f,
                SensorAreaMm2: 30.72f,
                PixelPitchMicrons: 1.4f,
                Megapixels: 12,
                HasAutoFocus: true,
                HasFlash: true,
                Vendor: vendor,
                DeviceModel: deviceModel,
                IsoNoiseMultiplier: 1.0f,
                VignetteCorrectionStrength: 0.0f,
                EdgeAcutanceBoost: 0.0f,
                SupportsUltraHighRes: (vendor == OemCameraVendor.XiaomiPocoRedmi),
                MaxNativeMegapixels: (vendor == OemCameraVendor.XiaomiPocoRedmi) ? 48 : 12,
                MaxNativeWidth: (vendor == OemCameraVendor.XiaomiPocoRedmi) ? 8000 : 4000,
                MaxNativeHeight: (vendor == OemCameraVendor.XiaomiPocoRedmi) ? 6000 : 3000
            ));

            backLenses.Add(new CameraLensProfile(
                DeviceId: backCam.DeviceId,
                MauiCamera: backCam,
                LensType: CameraLensType.Telephoto,
                ShortBadge: "2x",
                DisplayName: "Tele 2x",
                SpecSummary: "52mm • Zoom 2x",
                OpticalZoomRatio: 2.0f,
                TargetHardwareZoom: 2.0f,
                UsesLogicalZoomRatio: false,
                IsSensorCropZoom: true,
                FocalLengthMm: 4.5f,
                FocalLength35mmEquivalent: 52,
                Aperture: 1.8f,
                SensorWidthMm: 6.4f,
                SensorHeightMm: 4.8f,
                SensorAreaMm2: 30.72f,
                PixelPitchMicrons: 1.4f,
                Megapixels: 12,
                HasAutoFocus: true,
                HasFlash: true,
                Vendor: vendor,
                DeviceModel: deviceModel,
                IsoNoiseMultiplier: 1.15f,
                VignetteCorrectionStrength: 0.0f,
                EdgeAcutanceBoost: 0.35f
            ));
        }

        if (frontCam != null)
        {
            frontLenses.Add(new CameraLensProfile(
                DeviceId: frontCam.DeviceId,
                MauiCamera: frontCam,
                LensType: CameraLensType.FrontSelfie,
                ShortBadge: "1x",
                DisplayName: "Selfie",
                SpecSummary: "27mm f/2.2",
                OpticalZoomRatio: 1.0f,
                TargetHardwareZoom: 1.0f,
                UsesLogicalZoomRatio: false,
                IsSensorCropZoom: false,
                FocalLengthMm: 3.2f,
                FocalLength35mmEquivalent: 27,
                Aperture: 2.2f,
                SensorWidthMm: 4.1f,
                SensorHeightMm: 3.1f,
                SensorAreaMm2: 12.71f,
                PixelPitchMicrons: 1.2f,
                Megapixels: 5,
                HasAutoFocus: false,
                HasFlash: false,
                Vendor: vendor,
                DeviceModel: deviceModel,
                IsoNoiseMultiplier: 1.25f,
                VignetteCorrectionStrength: 0.0f,
                EdgeAcutanceBoost: 0.0f
            ));
        }
    }

    private static CameraLensProfile CreateProfile(
        RawLensCandidate cand,
        CameraLensType type,
        string shortBadge,
        string displayName,
        float opticalRatio,
        float targetZoom,
        bool usesLogicalZoom,
        bool isSensorCrop,
        OemCameraVendor vendor,
        string deviceModel)
    {
        float sensorArea = cand.SensorW * cand.SensorH;
        float pixelPitch = (cand.SensorW * 1000.0f) / Math.Max(cand.PixelW, 1);
        int equivFocal = (int)Math.Round(Calc35mmEquivalent(cand.FocalLength, cand.SensorW, cand.SensorH));
        if (isSensorCrop) equivFocal *= 2;
        int mp = (int)Math.Round((cand.PixelW * (double)cand.PixelH) / 1_000_000.0);

        string specSummary = $"{equivFocal}mm f/{cand.Aperture:0.#} • {mp}MP";

        // Tự động tính các hệ số hiệu chỉnh quang học:
        // 1. Hệ số nhân khử nhiễu ISO: Cảm biến nhỏ / điểm ảnh < 1.0µm cần khử nhiễu mạnh hơn
        float isoNoiseMult = 1.0f;
        if (pixelPitch < 1.1f)
            isoNoiseMult = 1.25f + (1.1f - pixelPitch) * 0.9f;
        else if (pixelPitch > 1.6f)
            isoNoiseMult = 0.85f;
        isoNoiseMult = Math.Clamp(isoNoiseMult, 0.8f, 2.2f);

        // 2. Bù tối 4 góc cho ống kính góc siêu rộng
        float vignetteStr = (type == CameraLensType.UltraWide) ? 0.28f : 0.0f;

        // 3. Tăng cường độ nét vi mô (Edge Acutance Boost) cho 2x Crop hoặc Macro
        float acutanceBoost = 0.0f;
        if (isSensorCrop) acutanceBoost = 0.38f;
        else if (type == CameraLensType.Macro) acutanceBoost = 0.42f;
        else if (type == CameraLensType.UltraWide) acutanceBoost = 0.14f;

        // Giảm acutance nếu là máy Samsung (vốn đã tăng nét phần cứng khá gắt)
        if (vendor == OemCameraVendor.Samsung)
        {
            acutanceBoost *= 0.65f;
        }

        // Kiểm tra khả năng chụp ảnh Siêu Phân Giải 48MP / 50MP / 64MP / 108MP
        bool supportsUltraHighRes = false;
        int maxNativeMp = mp;
        int maxNativeW = cand.PixelW;
        int maxNativeH = cand.PixelH;

        if (type == CameraLensType.WideMain)
        {
            try
            {
                if (cand.Characteristics.Get(CameraCharacteristics.ScalerStreamConfigurationMap) is global::Android.Hardware.Camera2.Params.StreamConfigurationMap map)
                {
                    var rawSizes = map.GetOutputSizes((int)global::Android.Graphics.ImageFormatType.RawSensor);
                    if (rawSizes != null)
                    {
                        foreach (var s in rawSizes)
                        {
                            if (s.Width >= 7000 || s.Height >= 5000)
                            {
                                supportsUltraHighRes = true;
                                maxNativeW = Math.Max(maxNativeW, s.Width);
                                maxNativeH = Math.Max(maxNativeH, s.Height);
                            }
                        }
                    }
                }
            }
            catch { }

            // Nhận diện cảm biến Quad-Bayer 48MP trên Xiaomi/POCO/Redmi
            if (vendor == OemCameraVendor.XiaomiPocoRedmi && (cand.PixelW >= 4000 || maxNativeW >= 7000))
            {
                supportsUltraHighRes = true;
                maxNativeMp = 48;
                maxNativeW = 8000;
                maxNativeH = 6000;
            }
            else if (supportsUltraHighRes)
            {
                maxNativeMp = (int)Math.Round((maxNativeW * (double)maxNativeH) / 1_000_000.0);
            }
        }

        return new CameraLensProfile(
            DeviceId: cand.DeviceId,
            MauiCamera: cand.MauiCamera,
            LensType: type,
            ShortBadge: shortBadge,
            DisplayName: displayName,
            SpecSummary: specSummary,
            OpticalZoomRatio: opticalRatio,
            TargetHardwareZoom: targetZoom,
            UsesLogicalZoomRatio: usesLogicalZoom,
            IsSensorCropZoom: isSensorCrop,
            FocalLengthMm: cand.FocalLength,
            FocalLength35mmEquivalent: equivFocal,
            Aperture: cand.Aperture,
            SensorWidthMm: cand.SensorW,
            SensorHeightMm: cand.SensorH,
            SensorAreaMm2: sensorArea,
            PixelPitchMicrons: pixelPitch,
            Megapixels: mp,
            HasAutoFocus: cand.HasAf,
            HasFlash: cand.HasFlash,
            Vendor: vendor,
            DeviceModel: deviceModel,
            IsoNoiseMultiplier: isoNoiseMult,
            VignetteCorrectionStrength: vignetteStr,
            EdgeAcutanceBoost: acutanceBoost,
            SupportsUltraHighRes: supportsUltraHighRes,
            MaxNativeMegapixels: maxNativeMp,
            MaxNativeWidth: maxNativeW,
            MaxNativeHeight: maxNativeH
        );
    }

    private static CameraLensProfile CreateLogicalProfile(
        RawLensCandidate baseCand,
        CameraLensType type,
        string shortBadge,
        string displayName,
        float opticalRatio,
        float targetZoom,
        bool usesLogicalZoom,
        bool isSensorCrop,
        OemCameraVendor vendor,
        string deviceModel)
    {
        // Cho Samsung / Pixel Logical Camera
        int equivFocal = (int)Math.Round(16.0f);
        string specSummary = $"{equivFocal}mm • Góc siêu rộng";

        return new CameraLensProfile(
            DeviceId: baseCand.DeviceId,
            MauiCamera: baseCand.MauiCamera,
            LensType: type,
            ShortBadge: shortBadge,
            DisplayName: displayName,
            SpecSummary: specSummary,
            OpticalZoomRatio: opticalRatio,
            TargetHardwareZoom: targetZoom,
            UsesLogicalZoomRatio: usesLogicalZoom,
            IsSensorCropZoom: isSensorCrop,
            FocalLengthMm: 1.8f,
            FocalLength35mmEquivalent: equivFocal,
            Aperture: 2.2f,
            SensorWidthMm: 4.0f,
            SensorHeightMm: 3.0f,
            SensorAreaMm2: 12.0f,
            PixelPitchMicrons: 1.12f,
            Megapixels: 8,
            HasAutoFocus: true,
            HasFlash: baseCand.HasFlash,
            Vendor: vendor,
            DeviceModel: deviceModel,
            IsoNoiseMultiplier: 1.35f,
            VignetteCorrectionStrength: 0.28f,
            EdgeAcutanceBoost: 0.15f
        );
    }

    private static float Calc35mmEquivalent(float focalMm, float sensorWMm, float sensorHMm)
    {
        float diag = MathF.Sqrt(sensorWMm * sensorWMm + sensorHMm * sensorHMm);
        if (diag < 0.1f) return 26.0f;
        float cropFactor = FullFrameDiagonalMm / diag;
        return focalMm * cropFactor;
    }

    private static Camera.MAUI.CameraInfo CreateMauiCameraInfo(
        string deviceId,
        string name,
        Camera.MAUI.CameraPosition position,
        bool hasFlash,
        CameraCharacteristics chars)
    {
        var info = new Camera.MAUI.CameraInfo();
        var t = typeof(Camera.MAUI.CameraInfo);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        t.GetProperty("DeviceId", flags)?.SetValue(info, deviceId);
        t.GetProperty("Name", flags)?.SetValue(info, name);
        t.GetProperty("Position", flags)?.SetValue(info, position);
        t.GetProperty("HasFlashUnit", flags)?.SetValue(info, hasFlash);
        t.GetProperty("MinZoomFactor", flags)?.SetValue(info, 1.0f);

        float maxZoom = 10.0f;
        if (chars.Get(CameraCharacteristics.ScalerAvailableMaxDigitalZoom) is Java.Lang.Number maxZNum)
        {
            maxZoom = maxZNum.FloatValue();
        }
        t.GetProperty("MaxZoomFactor", flags)?.SetValue(info, maxZoom);

        var resolutions = new List<Microsoft.Maui.Graphics.Size>();
        try
        {
            if (chars.Get(CameraCharacteristics.ScalerStreamConfigurationMap) is global::Android.Hardware.Camera2.Params.StreamConfigurationMap map)
            {
                var outputSizes = map.GetOutputSizes(Java.Lang.Class.FromType(typeof(global::Android.Media.ImageReader)));
                if (outputSizes != null)
                {
                    foreach (var s in outputSizes)
                    {
                        resolutions.Add(new Microsoft.Maui.Graphics.Size(s.Width, s.Height));
                    }
                }
            }
        }
        catch { }

        if (resolutions.Count == 0)
        {
            resolutions.Add(new Microsoft.Maui.Graphics.Size(1920, 1080));
            resolutions.Add(new Microsoft.Maui.Graphics.Size(1280, 720));
            resolutions.Add(new Microsoft.Maui.Graphics.Size(640, 480));
        }
        t.GetProperty("AvailableResolutions", flags)?.SetValue(info, resolutions);

        return info;
    }

    private record RawLensCandidate(
        Camera.MAUI.CameraInfo MauiCamera,
        string DeviceId,
        int Facing,
        float FocalLength,
        float Aperture,
        float SensorW,
        float SensorH,
        int PixelW,
        int PixelH,
        bool HasFlash,
        bool HasAf,
        float MinZoomRatio,
        float MaxZoomRatio,
        CameraCharacteristics Characteristics
    );
}
#endif
