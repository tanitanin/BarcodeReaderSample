using BarcodeReaderSample.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

// 空白ページの項目テンプレートについては、https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x411 を参照してください

namespace BarcodeReaderSample
{
    /// <summary>
    /// それ自体で使用できる空白ページまたはフレーム内に移動できる空白ページ。
    /// </summary>
    public sealed partial class MainPage : Page
    {
        MainPageViewModel ViewModel { get; } = new MainPageViewModel();

        public MainPage()
        {
            this.InitializeComponent();
            SizeChanged += OnWindowSizeChanged;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await ViewModel.InitializeAsync(Dispatcher, this.previewElement);
        }

        protected override async void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            await ViewModel.DeinitializeAsync();
        }

        private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
        {
            //ViewModel.SetWindowOrientation(e.NewSize);
        }

        private async void CaptureButton_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.SetNextCaptureDeviceAsync();
        }

        private async void PreviewElement_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var position = e.GetPosition(sender as UIElement);
            var control = sender as CaptureElement;
            var actualSize = new Size(control.ActualWidth, control.ActualHeight);
            await ViewModel.OnTapped(Window.Current.Bounds, actualSize, position, e.PointerDeviceType);
        }
    }
}
