using AVFoundation;
using CoreAnimation;
using CoreFoundation;

namespace Octopus.Mobile.iOS;

/// <summary>
/// Native iOS QR code scanner powered by Apple's <see cref="AVFoundation"/> framework (zero third-party dependencies,
/// NativeAOT-safe). Presents a full-screen modal camera viewfinder with torch toggle and resolves when a QR code is
/// detected or the user cancels.
/// </summary>
internal static class IosQrScanner
{
    public static Task<string?> ScanAsync()
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        UIApplication.SharedApplication.BeginInvokeOnMainThread(() => _ = PresentAsync(tcs));
        return tcs.Task;
    }

    private static async Task PresentAsync(TaskCompletionSource<string?> tcs)
    {
        try
        {
            var presenter = FindTopViewController();
            if (presenter is null)
            {
                tcs.TrySetResult(null);
                return;
            }

            var status = AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Video);
            if (status == AVAuthorizationStatus.NotDetermined)
            {
                var granted = await AVCaptureDevice.RequestAccessForMediaTypeAsync(AVAuthorizationMediaType.Video);
                status = granted ? AVAuthorizationStatus.Authorized : AVAuthorizationStatus.Denied;
            }

            if (status != AVAuthorizationStatus.Authorized)
            {
                ShowPermissionAlert(presenter, tcs);
                return;
            }

            if (AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Video) is null)
            {
                ShowUnavailableAlert(presenter, tcs);
                return;
            }

            var controller = new QrScannerViewController(tcs)
            {
                ModalPresentationStyle = UIModalPresentationStyle.FullScreen,
            };
            presenter.PresentViewController(controller, animated: true, completionHandler: null);
        }
        catch
        {
            tcs.TrySetResult(null);
        }
    }

    private static bool IsChinese =>
        NSLocale.PreferredLanguages.FirstOrDefault()?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true;

    private static void ShowPermissionAlert(UIViewController presenter, TaskCompletionSource<string?> tcs)
    {
        var title = IsChinese ? "需要相机权限" : "Camera access required";
        var message = IsChinese
            ? "请在系统“设置 → Octopus”中允许访问相机，以便扫描电脑上的配对二维码。"
            : "Allow camera access in Settings → Octopus to scan pairing QR codes from your computer.";
        var alert = UIAlertController.Create(title, message, UIAlertControllerStyle.Alert);
        alert.AddAction(UIAlertAction.Create(IsChinese ? "取消" : "Cancel", UIAlertActionStyle.Cancel, _ => tcs.TrySetResult(null)));
        alert.AddAction(UIAlertAction.Create(IsChinese ? "前往设置" : "Open Settings", UIAlertActionStyle.Default, _ =>
        {
            tcs.TrySetResult(null);
            if (NSUrl.FromString(UIApplication.OpenSettingsUrlString) is { } url)
            {
                UIApplication.SharedApplication.OpenUrl(url, new UIApplicationOpenUrlOptions(), null);
            }
        }));
        presenter.PresentViewController(alert, animated: true, completionHandler: null);
    }

    private static void ShowUnavailableAlert(UIViewController presenter, TaskCompletionSource<string?> tcs)
    {
        var title = IsChinese ? "未检测到相机" : "Camera unavailable";
        var message = IsChinese
            ? "当前设备没有可用的后置摄像头。"
            : "No camera is available on this device.";
        var alert = UIAlertController.Create(title, message, UIAlertControllerStyle.Alert);
        alert.AddAction(UIAlertAction.Create(IsChinese ? "知道了" : "OK", UIAlertActionStyle.Default, _ => tcs.TrySetResult(null)));
        presenter.PresentViewController(alert, animated: true, completionHandler: null);
    }

    private static UIViewController? FindTopViewController()
    {
        UIWindow? window = null;
        foreach (var scene in UIApplication.SharedApplication.ConnectedScenes)
        {
            if (scene is UIWindowScene windowScene && windowScene.ActivationState == UISceneActivationState.ForegroundActive)
            {
                window = windowScene.Windows.FirstOrDefault(w => w.IsKeyWindow) ?? windowScene.Windows.FirstOrDefault();
                if (window is not null) break;
            }
        }
        var current = window?.RootViewController;
        while (current?.PresentedViewController is { } presented)
        {
            current = presented;
        }
        return current;
    }

    private sealed class QrScannerViewController : UIViewController, IAVCaptureMetadataOutputObjectsDelegate
    {
        private readonly TaskCompletionSource<string?> _tcs;
        private readonly DispatchQueue _sessionQueue = new("si.octoterm.octopus.qr-scanner");
        private AVCaptureSession? _session;
        private AVCaptureDevice? _device;
        private AVCaptureVideoPreviewLayer? _previewLayer;
        private CAShapeLayer? _scrimLayer;
        private CAShapeLayer? _cornersLayer;
        private UILabel? _hintLabel;
        private UIButton? _closeButton;
        private UIButton? _torchButton;
        private bool _torchOn;
        private int _completed;

        public QrScannerViewController(TaskCompletionSource<string?> tcs) => _tcs = tcs;

        public override UIStatusBarStyle PreferredStatusBarStyle() => UIStatusBarStyle.LightContent;

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();
            View!.BackgroundColor = UIColor.Black;
            SetupCamera();
            SetupOverlay();
        }

        public override void ViewWillAppear(bool animated)
        {
            base.ViewWillAppear(animated);
            StartSession();
        }

        public override void ViewDidLayoutSubviews()
        {
            base.ViewDidLayoutSubviews();
            LayoutOverlay();
        }

        public override void ViewDidDisappear(bool animated)
        {
            base.ViewDidDisappear(animated);
            StopSession();
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                _tcs.TrySetResult(null);
            }
        }

        private void SetupCamera()
        {
            var device = AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Video);
            if (device is null) return;
            _device = device;

            var session = new AVCaptureSession();
            if (session.CanSetSessionPreset(AVCaptureSession.PresetHigh))
            {
                session.SessionPreset = AVCaptureSession.PresetHigh;
            }

            var input = AVCaptureDeviceInput.FromDevice(device, out var error);
            if (error is not null || input is null || !session.CanAddInput(input))
            {
                return;
            }
            session.AddInput(input);

            var output = new AVCaptureMetadataOutput();
            if (!session.CanAddOutput(output))
            {
                return;
            }
            session.AddOutput(output);
            output.SetDelegate(this, DispatchQueue.MainQueue);
            output.MetadataObjectTypes = AVMetadataObjectType.QRCode;

            var preview = new AVCaptureVideoPreviewLayer(session)
            {
                VideoGravity = AVLayerVideoGravity.ResizeAspectFill,
                Frame = View!.Bounds,
            };
            View.Layer.AddSublayer(preview);

            _session = session;
            _previewLayer = preview;
        }

        private void SetupOverlay()
        {
            var scrim = new CAShapeLayer
            {
                FillRule = CAShapeLayer.FillRuleEvenOdd,
                FillColor = UIColor.Black.ColorWithAlpha(0.56f).CGColor,
            };
            View!.Layer.AddSublayer(scrim);
            _scrimLayer = scrim;

            var corners = new CAShapeLayer
            {
                FillColor = UIColor.Clear.CGColor,
                StrokeColor = UIColor.White.CGColor,
                LineWidth = 3.5f,
                LineCap = CAShapeLayer.CapRound,
                LineJoin = CAShapeLayer.JoinRound,
            };
            View.Layer.AddSublayer(corners);
            _cornersLayer = corners;

            var hint = new UILabel
            {
                Text = IsChinese ? "将电脑上的配对二维码放入框内" : "Align the computer's pairing QR code inside the frame",
                TextColor = UIColor.White,
                Font = UIFont.SystemFontOfSize(15, UIFontWeight.Medium)!,
                TextAlignment = UITextAlignment.Center,
                Lines = 2,
            };
            View.AddSubview(hint);
            _hintLabel = hint;

            var close = CreateTopPillButton(IsChinese ? "取消" : "Cancel");
            close.TouchUpInside += (_, _) => Finish(null);
            View.AddSubview(close);
            _closeButton = close;

            if (_device is { HasTorch: true })
            {
                var torch = CreateTopPillButton(IsChinese ? "手电筒" : "Torch");
                torch.TouchUpInside += (_, _) => ToggleTorch();
                View.AddSubview(torch);
                _torchButton = torch;
            }
        }

        private static UIButton CreateTopPillButton(string title)
        {
            var button = new UIButton(UIButtonType.System);
            button.SetTitle(title, UIControlState.Normal);
            button.SetTitleColor(UIColor.White, UIControlState.Normal);
            button.TitleLabel!.Font = UIFont.SystemFontOfSize(15, UIFontWeight.Semibold)!;
            button.BackgroundColor = UIColor.Black.ColorWithAlpha(0.5f);
            button.Layer.CornerRadius = 18;
            button.Layer.MasksToBounds = true;
            return button;
        }

        private void LayoutOverlay()
        {
            var bounds = View!.Bounds;
            _previewLayer?.Frame = bounds;

            var side = Math.Min(bounds.Width * 0.70, 280.0);
            var frameRect = new CGRect(
                (bounds.Width - side) / 2.0,
                (bounds.Height - side) / 2.0 - 24.0,
                side,
                side);

            if (_scrimLayer is not null)
            {
                var full = UIBezierPath.FromRect(bounds);
                var cutout = UIBezierPath.FromRoundedRect(frameRect, 16);
                full.AppendPath(cutout);
                _scrimLayer.Frame = bounds;
                _scrimLayer.Path = full.CGPath;
            }

            if (_cornersLayer is not null)
            {
                var path = new UIBezierPath();
                const double arm = 24.0;
                // Top-left
                path.MoveTo(new CGPoint(frameRect.GetMinX(), frameRect.GetMinY() + arm));
                path.AddLineTo(new CGPoint(frameRect.GetMinX(), frameRect.GetMinY()));
                path.AddLineTo(new CGPoint(frameRect.GetMinX() + arm, frameRect.GetMinY()));
                // Top-right
                path.MoveTo(new CGPoint(frameRect.GetMaxX() - arm, frameRect.GetMinY()));
                path.AddLineTo(new CGPoint(frameRect.GetMaxX(), frameRect.GetMinY()));
                path.AddLineTo(new CGPoint(frameRect.GetMaxX(), frameRect.GetMinY() + arm));
                // Bottom-left
                path.MoveTo(new CGPoint(frameRect.GetMinX(), frameRect.GetMaxY() - arm));
                path.AddLineTo(new CGPoint(frameRect.GetMinX(), frameRect.GetMaxY()));
                path.AddLineTo(new CGPoint(frameRect.GetMinX() + arm, frameRect.GetMaxY()));
                // Bottom-right
                path.MoveTo(new CGPoint(frameRect.GetMaxX() - arm, frameRect.GetMaxY()));
                path.AddLineTo(new CGPoint(frameRect.GetMaxX(), frameRect.GetMaxY()));
                path.AddLineTo(new CGPoint(frameRect.GetMaxX(), frameRect.GetMaxY() - arm));
                _cornersLayer.Frame = bounds;
                _cornersLayer.Path = path.CGPath;
            }

            var topInset = View.SafeAreaInsets.Top + 12.0;
            _closeButton?.Frame = new CGRect(16, topInset, 76, 36);
            _torchButton?.Frame = new CGRect(bounds.Width - 96, topInset, 80, 36);
            _hintLabel?.Frame = new CGRect(24, frameRect.GetMaxY() + 24, bounds.Width - 48, 44);
        }

        private void ToggleTorch()
        {
            if (_device is not { HasTorch: true } device) return;
            if (!device.LockForConfiguration(out _)) return;
            try
            {
                _torchOn = !_torchOn;
                device.TorchMode = _torchOn ? AVCaptureTorchMode.On : AVCaptureTorchMode.Off;
                _torchButton?.SetTitle(
                    _torchOn ? (IsChinese ? "关闭照明" : "Torch On") : (IsChinese ? "手电筒" : "Torch"),
                    UIControlState.Normal);
            }
            finally
            {
                device.UnlockForConfiguration();
            }
        }

        private void StartSession()
        {
            if (_session is not { } session) return;
            _sessionQueue.DispatchAsync(() =>
            {
                if (!session.Running) session.StartRunning();
            });
        }

        private void StopSession()
        {
            if (_session is not { } session) return;
            _sessionQueue.DispatchAsync(() =>
            {
                if (session.Running) session.StopRunning();
            });
        }

        [Export("captureOutput:didOutputMetadataObjects:fromConnection:")]
        public void DidOutputMetadataObjects(AVCaptureMetadataOutput captureOutput, AVMetadataObject[] metadataObjects, AVCaptureConnection connection)
        {
            if (Volatile.Read(ref _completed) != 0) return;
            foreach (var obj in metadataObjects)
            {
                if (obj is AVMetadataMachineReadableCodeObject { StringValue: { Length: > 0 } value })
                {
                    var feedback = new UINotificationFeedbackGenerator();
                    feedback.NotificationOccurred(UINotificationFeedbackType.Success);
                    Finish(value);
                    return;
                }
            }
        }

        private void Finish(string? result)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            StopSession();
            DismissViewController(animated: true, () => _tcs.TrySetResult(result));
        }
    }
}
