using BarcodeReaderSample.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.PointOfService;
using Windows.Foundation;

namespace BarcodeReaderSample.Models
{
    internal class BarcodeDevice : ObservableObject
    {
        public BarcodeDevice(DeviceInformation info)
        {
            Name = info.Name;
            DeviceId = info.Id;
            EnclosureLocation = info.EnclosureLocation;
        }

        public string Name { get; }
        public string DeviceId { get; }
        public EnclosureLocation EnclosureLocation { get; }

        public async Task<BarcodeScanner> GetBarcodeScannerSync()
        {
            return await BarcodeScanner.FromIdAsync(DeviceId);
        }
    }
}
