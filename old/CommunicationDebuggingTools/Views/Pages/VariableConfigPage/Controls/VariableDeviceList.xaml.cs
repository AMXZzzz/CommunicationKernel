using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunicationDebuggingTools.Core.Enums;
using CommunicationDebuggingTools.Core.Models;
using CommunicationDebuggingTools.Core.Interfaces;

namespace CommunicationDebuggingTools.Views.VariableConfigPage.Controls {
    /// <summary>左侧设备列表。选中时触发 <see cref="DeviceSelected"/>。</summary>
    public partial class VariableDeviceList : UserControl {
        /// <summary>由页面注入。</summary>
        public IDeviceService DeviceService { get; set; }
        /// <summary>由页面注入。</summary>
        public IVariableService VariableService { get; set; }

        public event Action<string> DeviceSelected;

        private readonly ObservableCollection<Row> _items = new ObservableCollection<Row>();
        private string _selectedId;

        public VariableDeviceList () {
            InitializeComponent();
            listDevices.ItemsSource = _items;
        }

        /// <summary>重新加载设备与变量数量；尽量保持选中。</summary>
        public void Reload () {
            string keep = _selectedId;
            _items.Clear();

            if (DeviceService != null) {
                foreach (DeviceInfo d in DeviceService.Devices) {
                    if (d == null) continue;
                    _items.Add(Row.From(d, CountVars(d.Id), BrushOf));
                }
            }

            txtCount.Text = _items.Count.ToString();

            if (!string.IsNullOrEmpty(keep)) {
                var m = _items.FirstOrDefault(x => x.Id == keep);
                if (m != null) {
                    listDevices.SelectedItem = m;
                    return;
                }
            }

            if (_items.Count > 0)
                listDevices.SelectedIndex = 0;
        }

        private void ListDevices_SelectionChanged (object sender, SelectionChangedEventArgs e) {
            var row = listDevices.SelectedItem as Row;
            _selectedId = row != null ? row.Id : null;
            if (DeviceSelected != null)
                DeviceSelected(_selectedId);
        }

        private int CountVars (string deviceId) {
            if (VariableService == null || string.IsNullOrEmpty(deviceId))
                return 0;
            return VariableService.Variables.Count(v => v != null && v.DeviceId == deviceId);
        }

        private Brush BrushOf (DeviceStatusType status) {
            string key = "SF.Brush.Text.Secondary";
            if (status == DeviceStatusType.Success) key = "SF.Brush.Status.Success";
            else if (status == DeviceStatusType.Error) key = "SF.Brush.Status.Error";
            else if (status == DeviceStatusType.Warning || status == DeviceStatusType.Connecting)
                key = "SF.Brush.Status.Warning";

            return TryFindResource(key) as Brush
                   ?? new SolidColorBrush(Color.FromRgb(0x9D, 0x9D, 0x9D));
        }

        private sealed class Row {
            public string Id { get; set; }
            public string Name { get; set; }
            public string SubTitle { get; set; }
            public string VariableCountText { get; set; }
            public Brush StatusBrush { get; set; }

            public static Row From (DeviceInfo d, int count, Func<DeviceStatusType, Brush> brush) {
                return new Row {
                    Id = d.Id,
                    Name = string.IsNullOrEmpty(d.Name) ? d.Id : d.Name,
                    SubTitle = d.Model ?? "",
                    VariableCountText = count + " 个变量",
                    StatusBrush = brush(d.StatusType)
                };
            }
        }
    }
}