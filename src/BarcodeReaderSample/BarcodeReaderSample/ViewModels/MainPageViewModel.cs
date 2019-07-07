using BarcodeReaderSample.Helpers;
using BarcodeReaderSample.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Devices.Enumeration;
using Windows.Devices.Input;
using Windows.Devices.PointOfService;
using Windows.Foundation;
using Windows.Graphics.Display;
using Windows.Media.Capture;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.System.Display;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace BarcodeReaderSample.ViewModels
{
    internal class MainPageViewModel : ObservableObject
    {
        #region Properties and Fields

        private CoreDispatcher Dispatcher { get; set; } = CoreWindow.GetForCurrentThread().Dispatcher;

        private CaptureElement PreviewControl { get; set; } = null;

        public bool IsLandscape
        {
            get => this.isLandscape;
            set
            {
                Set(ref this.isLandscape, value);
                RaisePropertyChanged(nameof(IsPortrait));
            }
        }
        private bool isLandscape = true;

        public bool IsPortrait
        {
            get => !IsLandscape;
        }

        public ObservableCollection<BarcodeDevice> BarcodeDevices { get; } = new ObservableCollection<BarcodeDevice>();

        private MediaCapture mediaCapture = null;

        public FlowDirection FlowDirection
        {
            get => this.flowDirection;
            private set => Set(ref this.flowDirection, value);
        }
        private FlowDirection flowDirection = FlowDirection.LeftToRight;

        private DisplayRequest displayRequest = new DisplayRequest();
        private BarcodeDevice selectedBarcodeDevice = null;
        private DeviceWatcher barcodeDeviceWatcher = null;
        private BarcodeScanner selectedScanner = null;
        private ClaimedBarcodeScanner claimedBarcodeScanner = null;

        private bool isFocused = false;
        private bool isAutoFocus = false;

        public bool IsPreviewing
        {
            get => this.isPreviewing;
            set => Set(ref this.isPreviewing, value);
        }
        private bool isPreviewing = false;

        public bool IsBarcodeScanning
        {
            get => this.isBarcodeScanning;
            set => Set(ref this.isBarcodeScanning, value);
        }
        private bool isBarcodeScanning = false;

        private bool _externalCamera;
        private bool _mirroringPreview;
        private DeviceInformation _cameraDevice;
        private CameraRotationHelper _rotationHelper;

        public bool IsScannerClaimed { get; private set; } = false;
        public bool ScannerSupportsPreview { get; private set; } = false;
        public bool SoftwareTriggerStarted { get; private set; } = false;

        static readonly Guid rotationGuid = new Guid("C380465D-2271-428C-9B83-ECEA3B4A85C1");

        bool isSelectionChanging = false;
        string pendingSelectionDeviceId = null;
        bool isStopPending = false;

        #endregion

        public MainPageViewModel()
        {
            App.Current.EnteredBackground += OnEnteredBackground;
            App.Current.LeavingBackground += OnLeavingBackground;
            App.Current.Suspending += OnSuspending;
            App.Current.Resuming += OnResuming;
        }

        #region Page event

        /// <summary>
        /// OnNavigateTo
        /// </summary>
        /// <param name="dispatcher"></param>
        /// <returns></returns>
        public async Task InitializeAsync(CoreDispatcher dispatcher, CaptureElement control)
        {
            Dispatcher = dispatcher;
            PreviewControl = control;

            var selector = BarcodeScanner.GetDeviceSelector();

            barcodeDeviceWatcher = DeviceInformation.CreateWatcher(selector);
            barcodeDeviceWatcher.Added += OnBarcodeScannerAdded;
            barcodeDeviceWatcher.Removed += OnBarcodeScannerRemoved;
            barcodeDeviceWatcher.Updated += OnBarcodeScannerUpdated;

            barcodeDeviceWatcher.Start();
        }

        /// <summary>
        /// OnNavigatedFrom
        /// </summary>
        /// <returns></returns>
        public async Task DeinitializeAsync()
        {
            await CleanupCameraAsync();

            barcodeDeviceWatcher.Stop();

            if (isSelectionChanging)
            {
                // If selection is changing, then let it know to stop media capture
                // when it's done.
                isStopPending = true;
            }
            else
            {
                // If selection is not changing, then it's safe to stop immediately.
                await CloseScannerResourcesAsync();
            }
        }

        /// <summary>
        /// SizeChanged
        /// </summary>
        /// <param name="size"></param>
        public void SetWindowOrientation(Size size)
        {
            if (size.Width >= size.Height)
            {
                IsLandscape = true;
            }
            else
            {
                IsLandscape = false;
            }
        }

        /// <summary>
        /// ButtonClicked
        /// </summary>
        /// <returns></returns>
        public async Task SetNextCaptureDeviceAsync()
        {
            if (selectedBarcodeDevice == null)
            {
                selectedBarcodeDevice = BarcodeDevices.FirstOrDefault();
            }
            else
            {
                lock (BarcodeDevices)
                {
                    var oldIndex = BarcodeDevices.IndexOf(selectedBarcodeDevice);
                    var newIndex = (oldIndex + 1) % BarcodeDevices.Count;
                    selectedBarcodeDevice = BarcodeDevices.ElementAtOrDefault(newIndex);
                }
            }

            var selectedScannerInfo = selectedBarcodeDevice;
            var deviceId = selectedScannerInfo.DeviceId;

            if (isSelectionChanging)
            {
                pendingSelectionDeviceId = deviceId;
                return;
            }

            do
            {
                await SelectScannerAsync(deviceId);

                // Stop takes precedence over updating the selection.
                if (isStopPending)
                {
                    await CloseScannerResourcesAsync();
                    break;
                }

                deviceId = pendingSelectionDeviceId;
                pendingSelectionDeviceId = null;
            } while (!String.IsNullOrEmpty(deviceId));
        }

        #endregion

        #region Application

        private async void OnEnteredBackground(object sender, EnteredBackgroundEventArgs e)
        {
            var deferral = e.GetDeferral();
            await CleanupCameraAsync();
            deferral.Complete();
        }

        private async void OnLeavingBackground(object sender, LeavingBackgroundEventArgs e)
        {
            var deferral = e.GetDeferral();
            var selectedScannerInfo = selectedBarcodeDevice;
            if (selectedScannerInfo != null)
            {
                var deviceId = selectedScannerInfo.DeviceId;

                if (isSelectionChanging)
                {
                    pendingSelectionDeviceId = deviceId;
                    return;
                }

                await SelectScannerAsync(deviceId);
            }
            deferral.Complete();
        }

        private async void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            await CleanupCameraAsync();
            deferral.Complete();
        }

        private async void OnResuming(object sender, object e)
        {
            var selectedScannerInfo = selectedBarcodeDevice;
            if (selectedScannerInfo != null)
            {
                var deviceId = selectedScannerInfo.DeviceId;

                if (isSelectionChanging)
                {
                    pendingSelectionDeviceId = deviceId;
                    return;
                }

                await SelectScannerAsync(deviceId);
            }
        }

        #endregion

        #region DeviceWatcher event

        private async void OnBarcodeScannerAdded(DeviceWatcher sender, DeviceInformation args)
        {
            await Dispatcher?.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                BarcodeDevices.Add(new BarcodeDevice(args));

                // Select the first scanner by default.
                if (BarcodeDevices.Count == 1)
                {
                    //ScannerListBox.SelectedIndex = 0;
                    await SetNextCaptureDeviceAsync();
                }
            });
        }

        private async void OnBarcodeScannerRemoved(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            await Dispatcher?.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                if (selectedBarcodeDevice.DeviceId == args.Id)
                {
                    await CloseScannerResourcesAsync();
                }

                var oldIndex = BarcodeDevices.IndexOf(BarcodeDevices.SingleOrDefault(x => x.DeviceId == args.Id));
                BarcodeDevices.Remove(BarcodeDevices.SingleOrDefault(x => x.DeviceId == args.Id));

                if (BarcodeDevices.Count > 0)
                {
                    var newDevice = BarcodeDevices.ElementAtOrDefault(oldIndex) ?? BarcodeDevices.FirstOrDefault();
                    //ScannerListBox.SelectedIndex = 0;
                }
            });
        }

        private async void OnBarcodeScannerUpdated(DeviceWatcher sender, DeviceInformationUpdate args)
        {
        }

        #endregion

        #region Camera Preview

        private async Task StartPreviewAsync()
        {
            try
            {
                mediaCapture = new MediaCapture();
                // Register for a notification when something goes wrong
                mediaCapture.Failed += MediaCapture_Failed;

                // Handle camera device location
                _cameraDevice = await DeviceInformation.CreateFromIdAsync(selectedScanner.VideoDeviceId);
                if (_cameraDevice != null)
                {
                    if (_cameraDevice.EnclosureLocation == null ||
                        _cameraDevice.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Unknown)
                    {
                        _externalCamera = true;
                    }
                    else
                    {
                        _externalCamera = false;
                        _mirroringPreview = (_cameraDevice.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Front);
                    }

                    _rotationHelper = new CameraRotationHelper(_cameraDevice.EnclosureLocation);
                    _rotationHelper.OrientationChanged += RotationHelper_OrientationChanged;
                }

                var settings = new MediaCaptureInitializationSettings();
                settings.VideoDeviceId = selectedScanner.VideoDeviceId;
                settings.StreamingCaptureMode = StreamingCaptureMode.Video;
                settings.SharingMode = MediaCaptureSharingMode.ExclusiveControl;

                await mediaCapture.InitializeAsync(settings);

                displayRequest.RequestActive();
                //DisplayInformation.AutoRotationPreferences = DisplayOrientations.Landscape;
            }
            catch (UnauthorizedAccessException)
            {
                // This will be thrown if the user denied access to the camera in privacy settings
                //ShowMessageToUser("The app was denied access to the camera");
                return;
            }

            try
            {
                //PreviewControl.Source = mediaCapture;
                //PreviewControl.FlowDirection = _mirroringPreview ? FlowDirection.RightToLeft : FlowDirection.LeftToRight; // to rotation
                if (PreviewControl != null)
                {
                    PreviewControl.Source = mediaCapture;
                }
                await mediaCapture.StartPreviewAsync();
                //await SetPreviewRotationAsync();
                await SetPreviewRotationAsync(DisplayInformation.GetForCurrentView().CurrentOrientation);
                IsPreviewing = true;
            }
            catch (System.IO.FileLoadException)
            {
                mediaCapture.CaptureDeviceExclusiveControlStatusChanged += _mediaCapture_CaptureDeviceExclusiveControlStatusChanged;
            }

            // 連続オートフォーカス
            if (IsPreviewing)
            {
                var focusControl = mediaCapture.VideoDeviceController.FocusControl;
                if (focusControl.Supported)
                {
                    await focusControl.UnlockAsync();
                    var settings = new FocusSettings { Mode = FocusMode.Continuous, AutoFocusRange = AutoFocusRange.FullRange };
                    focusControl.Configure(settings);
                    await focusControl.FocusAsync();
                    isAutoFocus = true;
                }
            }

            // 光学式手ブレ補正
            if (IsPreviewing)
            {
                if (mediaCapture.VideoDeviceController.OpticalImageStabilizationControl.Supported)
                {
                    var stabilizationModes = mediaCapture.VideoDeviceController.OpticalImageStabilizationControl.SupportedModes;

                    if (stabilizationModes.Contains(OpticalImageStabilizationMode.Auto))
                    {
                        mediaCapture.VideoDeviceController.OpticalImageStabilizationControl.Mode = OpticalImageStabilizationMode.Auto;
                    }
                }
            }

            // ホワイトバランス自動調整
            if (IsPreviewing)
            {
                var whiteBalanceControl = mediaCapture.VideoDeviceController.WhiteBalanceControl;
                if (whiteBalanceControl.Supported)
                {
                    await whiteBalanceControl.SetPresetAsync(ColorTemperaturePreset.Auto);
                }
            }
        }

        /// <summary>
        /// Media capture failed, potentially due to the camera being unplugged.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="errorEventArgs"></param>
        private void MediaCapture_Failed(MediaCapture sender, MediaCaptureFailedEventArgs errorEventArgs)
        {
            //rootPage.NotifyUser("Media capture failed. Make sure the camera is still connected.", NotifyType.ErrorMessage);
        }

        private async void _mediaCapture_CaptureDeviceExclusiveControlStatusChanged(MediaCapture sender, MediaCaptureDeviceExclusiveControlStatusChangedEventArgs args)
        {
            if (args.Status == MediaCaptureDeviceExclusiveControlStatus.SharedReadOnlyAvailable)
            {
                //ShowMessageToUser("The camera preview can't be displayed because another app has exclusive access");
            }
            else if (args.Status == MediaCaptureDeviceExclusiveControlStatus.ExclusiveControlAvailable && !isPreviewing)
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                {
                    await StartPreviewAsync();
                });
            }
        }

        private async Task CleanupCameraAsync()
        {
            if (mediaCapture != null)
            {
                if (IsPreviewing)
                {
                    await mediaCapture.StopPreviewAsync();
                }

                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (IsPreviewing)
                    {
                        IsPreviewing = false;
                    }
                    if (PreviewControl != null)
                    {
                        PreviewControl.Source = null;
                    }
                    if (displayRequest != null)
                    {
                        displayRequest.RequestRelease();
                    }

                    mediaCapture.Dispose();
                    mediaCapture = null;
                });
            }
        }

        #endregion

        #region Rotation

        /// <summary>
        /// Set preview rotation and mirroring state to adjust for the orientation of the camera, and for embedded cameras, the rotation of the device.
        /// </summary>
        /// <param name="message"></param>
        /// <param name="type"></param>
        private async Task SetPreviewRotationAsync(DisplayOrientations displayOrientation)
        {
            bool isExternalCamera;
            bool isPreviewMirrored;

            // Figure out where the camera is located to account for mirroring and later adjust rotation accordingly.
            var cameraInformation = await DeviceInformation.CreateFromIdAsync(selectedScanner.VideoDeviceId);

            if ((cameraInformation.EnclosureLocation == null) || (cameraInformation.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Unknown))
            {
                isExternalCamera = true;
                isPreviewMirrored = false;
            }
            else
            {
                isExternalCamera = false;
                isPreviewMirrored = (cameraInformation.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Front);
            }

            //PreviewControl.FlowDirection = isPreviewMirrored ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            if (PreviewControl != null)
            {
                PreviewControl.FlowDirection = isPreviewMirrored ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            }

            if (!isExternalCamera)
            {
                // Calculate which way and how far to rotate the preview.
                int rotationDegrees = 0;
                switch (displayOrientation)
                {
                    case DisplayOrientations.Portrait:
                        rotationDegrees = 90;
                        break;
                    case DisplayOrientations.LandscapeFlipped:
                        rotationDegrees = 180;
                        break;
                    case DisplayOrientations.PortraitFlipped:
                        rotationDegrees = 270;
                        break;
                    case DisplayOrientations.Landscape:
                    default:
                        rotationDegrees = 0;
                        break;
                }

                // The rotation direction needs to be inverted if the preview is being mirrored.
                if (isPreviewMirrored)
                {
                    rotationDegrees = (360 - rotationDegrees) % 360;
                }

                // Add rotation metadata to the preview stream to make sure the aspect ratio / dimensions match when rendering and getting preview frames.
                var streamProperties = mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview);
                streamProperties.Properties[rotationGuid] = rotationDegrees;
                await mediaCapture.SetEncodingPropertiesAsync(MediaStreamType.VideoPreview, streamProperties, null);
            }
        }

        private async Task SetPreviewRotationAsync()
        {
            if (!_externalCamera)
            {
                // Add rotation metadata to the preview stream to make sure the aspect ratio / dimensions match when rendering and getting preview frames
                var rotation = _rotationHelper.GetCameraPreviewOrientation();
                var props = mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview);
                Guid RotationKey = new Guid("C380465D-2271-428C-9B83-ECEA3B4A85C1");
                props.Properties.Add(RotationKey, CameraRotationHelper.ConvertSimpleOrientationToClockwiseDegrees(rotation));
                await mediaCapture.SetEncodingPropertiesAsync(MediaStreamType.VideoPreview, props, null);
            }
        }

        private async void RotationHelper_OrientationChanged(object sender, bool updatePreview)
        {
            if (updatePreview)
            {
                await SetPreviewRotationAsync();
            }
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => {
                // Rotate the buttons in the UI to match the rotation of the device
                var angle = CameraRotationHelper.ConvertSimpleOrientationToClockwiseDegrees(_rotationHelper.GetUIOrientation());
                var transform = new RotateTransform { Angle = angle };

                // The RenderTransform is safe to use (i.e. it won't cause layout issues) in this case, because these buttons have a 1:1 aspect ratio
                //CapturePhotoButton.RenderTransform = transform;
                //CapturePhotoButton.RenderTransform = transform;
            });
        }

        #endregion

        #region TapToFocus

        public async Task OnTapped(Rect window, Size actualSize, Point position, PointerDeviceType deviceType)
        {
            if (!isPreviewing)
            {
                return;
            }

            var focusControl = mediaCapture.VideoDeviceController.FocusControl;
            if (focusControl.Supported)
            {
                if (isAutoFocus)
                {
                    await focusControl.LockAsync();
                    isAutoFocus = false;
                }

                if (!isFocused && focusControl.FocusState != MediaCaptureFocusState.Searching)
                {
                    var smallEdge = Math.Min(window.Width, window.Height);

                    // Choose to make the focus rectangle 1/4th the length of the shortest edge of the window
                    var size = new Size(smallEdge / 4, smallEdge / 4);

                    // Note that at this point, a rect at "position" with size "size" could extend beyond the preview area. The following method will reposition the rect if that is the case
                    await TapToFocus(position, size, actualSize);
                }
                else
                {
                    await TapUnfocus();
                }
            }
        }

        public async Task TapToFocus(Point position, Size size, Size actualControlSize)
        {
            isFocused = true;

            var previewRect = GetPreviewStreamRectInControl(actualControlSize);
            var focusPreview = ConvertUiTapToPreviewRect(position, size, previewRect);

            // Note that this Region Of Interest could be configured to also calculate exposure 
            // and white balance within the region
            var regionOfInterest = new RegionOfInterest
            {
                AutoFocusEnabled = true,
                BoundsNormalized = true,
                Bounds = focusPreview,
                Type = RegionOfInterestType.Unknown,
                Weight = 100,
            };

            var focusControl = mediaCapture.VideoDeviceController.FocusControl;
            var focusRange = focusControl.SupportedFocusRanges.Contains(AutoFocusRange.FullRange) ? AutoFocusRange.FullRange : focusControl.SupportedFocusRanges.FirstOrDefault();
            var focusMode = focusControl.SupportedFocusModes.Contains(FocusMode.Single) ? FocusMode.Single : focusControl.SupportedFocusModes.FirstOrDefault();
            var settings = new FocusSettings { Mode = focusMode, AutoFocusRange = focusRange };
            focusControl.Configure(settings);

            var roiControl = mediaCapture.VideoDeviceController.RegionsOfInterestControl;
            await roiControl.SetRegionsAsync(new[] { regionOfInterest }, true);

            await focusControl.FocusAsync();
        }

        private async Task TapUnfocus()
        {
            isFocused = false;

            var roiControl = mediaCapture.VideoDeviceController.RegionsOfInterestControl;
            await roiControl.ClearRegionsAsync();

            var focusControl = mediaCapture.VideoDeviceController.FocusControl;
            await focusControl.FocusAsync();
        }

        public Rect GetPreviewStreamRectInControl(Size actualControlSize)
        {
            var result = new Rect();

            var previewResolution = mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview) as VideoEncodingProperties;

            // In case this function is called before everything is initialized correctly, return an empty result
            if (!isPreviewing || actualControlSize.Height < 1 || actualControlSize.Width < 1 ||
                previewResolution == null || previewResolution.Height == 0 || previewResolution.Width == 0)
            {
                return result;
            }

            var streamWidth = previewResolution.Width;
            var streamHeight = previewResolution.Height;

            var displayOrientation = DisplayInformation.GetForCurrentView().CurrentOrientation;

            // For portrait orientations, the width and height need to be swapped
            if (displayOrientation == DisplayOrientations.Portrait || displayOrientation == DisplayOrientations.PortraitFlipped)
            {
                streamWidth = previewResolution.Height;
                streamHeight = previewResolution.Width;
            }

            // Start by assuming the preview display area in the control spans the entire width and height both (this is corrected in the next if for the necessary dimension)
            result.Width = actualControlSize.Width;
            result.Height = actualControlSize.Height;

            // If UI is "wider" than preview, letterboxing will be on the sides
            if ((actualControlSize.Width / actualControlSize.Height > streamWidth / (double)streamHeight))
            {
                var scale = actualControlSize.Height / streamHeight;
                var scaledWidth = streamWidth * scale;

                result.X = (actualControlSize.Width - scaledWidth) / 2.0;
                result.Width = scaledWidth;
            }
            else // Preview stream is "wider" than UI, so letterboxing will be on the top+bottom
            {
                var scale = actualControlSize.Width / streamWidth;
                var scaledHeight = streamHeight * scale;

                result.Y = (actualControlSize.Height - scaledHeight) / 2.0;
                result.Height = scaledHeight;
            }

            return result;
        }

        private Rect ConvertUiTapToPreviewRect(Point tap, Size size, Rect previewRect)
        {
            // Adjust for the resulting focus rectangle to be centered around the position
            double left = tap.X - size.Width / 2, top = tap.Y - size.Height / 2;

            // Get the information about the active preview area within the CaptureElement (in case it's letterboxed)
            double previewWidth = previewRect.Width, previewHeight = previewRect.Height;
            double previewLeft = previewRect.Left, previewTop = previewRect.Top;

            var orientation = DisplayInformation.GetForCurrentView().CurrentOrientation;

            // Transform the left and top of the tap to account for rotation
            switch (orientation)
            {
                case DisplayOrientations.Portrait:
                    var tempLeft = left;

                    left = top;
                    top = previewRect.Width - tempLeft;
                    break;
                case DisplayOrientations.LandscapeFlipped:
                    left = previewRect.Width - left;
                    top = previewRect.Height - top;
                    break;
                case DisplayOrientations.PortraitFlipped:
                    var tempTop = top;

                    top = left;
                    left = previewRect.Width - tempTop;
                    break;
            }

            // For portrait orientations, the information about the active preview area needs to be rotated
            if (orientation == DisplayOrientations.Portrait || orientation == DisplayOrientations.PortraitFlipped)
            {
                previewWidth = previewRect.Height;
                previewHeight = previewRect.Width;
                previewLeft = previewRect.Top;
                previewTop = previewRect.Left;
            }

            // Normalize width and height of the focus rectangle
            var width = size.Width / previewWidth;
            var height = size.Height / previewHeight;

            // Shift rect left and top to be relative to just the active preview area
            left -= previewLeft;
            top -= previewTop;

            // Normalize left and top
            left /= previewWidth;
            top /= previewHeight;

            // Ensure rectangle is fully contained within the active preview area horizontally
            left = Math.Max(left, 0);
            left = Math.Min(1 - width, left);

            // Ensure rectangle is fully contained within the active preview area vertically
            top = Math.Max(top, 0);
            top = Math.Min(1 - height, top);

            // Create and return resulting rectangle
            return new Rect(left, top, width, height);
        }

        #endregion

        #region BarcodeScanner

        /// <summary>
        /// Close the scanners and stop the preview.
        /// </summary>
        private async Task CloseScannerResourcesAsync()
        {
            await StopBarcodeScanAsync();

            claimedBarcodeScanner?.Dispose();
            claimedBarcodeScanner = null;

            selectedScanner?.Dispose();
            selectedScanner = null;

            SoftwareTriggerStarted = false;
            RaisePropertyChanged(nameof(SoftwareTriggerStarted));

            if (IsPreviewing)
            {
                if (mediaCapture != null)
                {
                    await mediaCapture.StopPreviewAsync();
                    mediaCapture.Dispose();
                    mediaCapture = null;
                }

                // Allow the display to go to sleep.
                displayRequest.RequestRelease();

                IsPreviewing = false;
                //RaisePropertyChanged(nameof(IsPreviewing));
            }
        }

        /// <summary>
        /// Event Handler for Flip Preview Button Click.
        /// Stops scanning.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void FlipPreview_Click(object sender, RoutedEventArgs e)
        {
            //if (PreviewControl.FlowDirection == FlowDirection.LeftToRight)
            //{
            //    PreviewControl.FlowDirection = FlowDirection.RightToLeft;
            //}
            //else
            //{
            //    PreviewControl.FlowDirection = FlowDirection.LeftToRight;
            //}
        }

        /// <summary>
        /// Select the scanner specified by its device ID.
        /// </summary>
        /// <param name="scannerDeviceId"></param>
        private async Task SelectScannerAsync(string scannerDeviceId)
        {
            isSelectionChanging = true;

            await CloseScannerResourcesAsync();

            selectedScanner = await BarcodeScanner.FromIdAsync(scannerDeviceId);

            if (selectedScanner != null)
            {
                claimedBarcodeScanner = await selectedScanner.ClaimScannerAsync();
                if (claimedBarcodeScanner != null)
                {
                    await claimedBarcodeScanner.EnableAsync();
                    claimedBarcodeScanner.Closed += ClaimedScanner_Closed;
                    ScannerSupportsPreview = !String.IsNullOrEmpty(selectedScanner.VideoDeviceId);
                    RaisePropertyChanged(nameof(ScannerSupportsPreview));

                    claimedBarcodeScanner.DataReceived += ClaimedScanner_DataReceived;

                    if (ScannerSupportsPreview)
                    {
                        //await StartMediaCaptureAsync(selectedScanner.VideoDeviceId);
                        await StartPreviewAsync();

                        await StartBarcodeScanAsync();
                    }
                }
                else
                {
                    //rootPage.NotifyUser("Failed to claim the selected barcode scanner", NotifyType.ErrorMessage);
                }

            }
            else
            {
                //rootPage.NotifyUser("Failed to create a barcode scanner object", NotifyType.ErrorMessage);
            }

            IsScannerClaimed = claimedBarcodeScanner != null;
            RaisePropertyChanged(nameof(IsScannerClaimed));

            isSelectionChanging = false;
        }

        /// <summary>
        /// Closed notification was received from the selected scanner.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private void ClaimedScanner_Closed(ClaimedBarcodeScanner sender, ClaimedBarcodeScannerClosedEventArgs args)
        {
            // Resources associated to the claimed barcode scanner can be cleaned up here
        }

        /// <summary>
        /// Scan data was received from the selected scanner.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private async void ClaimedScanner_DataReceived(ClaimedBarcodeScanner sender, BarcodeScannerDataReceivedEventArgs args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                //ScenarioOutputScanDataLabel.Text = DataHelpers.GetDataLabelString(args.Report.ScanDataLabel, args.Report.ScanDataType);
                //ScenarioOutputScanData.Text = DataHelpers.GetDataString(args.Report.ScanData);
                //ScenarioOutputScanDataType.Text = BarcodeSymbologies.GetName(args.Report.ScanDataType);
            });
        }

        private async Task StartBarcodeScanAsync()
        {
            if (claimedBarcodeScanner != null)
            {
                await claimedBarcodeScanner.StartSoftwareTriggerAsync();

                SoftwareTriggerStarted = true;
                RaisePropertyChanged(nameof(SoftwareTriggerStarted));
            }
        }

        private async Task StopBarcodeScanAsync()
        {
            if (claimedBarcodeScanner != null)
            {
                await claimedBarcodeScanner.StartSoftwareTriggerAsync();

                SoftwareTriggerStarted = false;
                RaisePropertyChanged(nameof(SoftwareTriggerStarted));
            }
        }

        #endregion

    }
}
