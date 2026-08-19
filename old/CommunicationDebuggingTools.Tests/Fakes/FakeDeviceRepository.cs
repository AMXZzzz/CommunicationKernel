using System.Collections.Generic;
using CommunicationDebuggingTools.Core.Interfaces;
using CommunicationDebuggingTools.Core.Models;

namespace CommunicationDebuggingTools.Tests.Fakes {
    public class FakeDeviceRepository : IDeviceRepository {
        public List<DeviceInfo> Items { get; set; }

        public FakeDeviceRepository () {
            Items = new List<DeviceInfo>();
        }

        public IList<DeviceInfo> LoadAll () {
            return new List<DeviceInfo>(Items);
        }

        public void SaveAll (IList<DeviceInfo> devices) {
            Items = new List<DeviceInfo>(devices);
        }
    }
}