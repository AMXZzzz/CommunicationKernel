#nullable disable

// -----------------------------------------------------------------------------
// 文件: Views/Pages/Variable/Controls/VariableDeviceList.xaml.cs
// 层级: UI 层 — WPF Views
// 作用: 左侧设备列表；选中后发出 DeviceSelected，尽量保持原选中项。
// -----------------------------------------------------------------------------

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunicationKernel.UI.Wpf.Core.Enums;
using CommunicationKernel.UI.Wpf.Core.Models;
using CommunicationKernel.UI.Wpf.Core.Interfaces;

namespace CommunicationKernel.UI.Wpf.Views.Pages.Variable.Controls {
    /// <summary>左侧设备列表。选中时触发 <see cref="DeviceSelected"/>。</summary>
    public partial class VariableDeviceList : UserControl {
        /// <summary>由页面注入。</summary>
        public IDeviceService DeviceService { get; set; }
        /// <summary>由页面注入。</summary>
        public IVariableService VariableService { get; set; }

        /// <summary>选中设备变化，参数为设备 Id；无选中时为 null。</summary>
        public event Action<string> DeviceSelected;

        /// <summary>列表行，绑定到 listDevices。</summary>
        private readonly ObservableCollection<Row> _items = new ObservableCollection<Row>();

        /// <summary>当前选中的设备 Id。<see cref="Reload"/> 靠它在刷新后还原选中项。</summary>
        private string _selectedId;

        /// <summary>构造：加载 XAML 并把行集合绑定到列表。</summary>
        public VariableDeviceList () {
            // 解析 XAML 并把行集合绑到 ListBox
            InitializeComponent();
            listDevices.ItemsSource = _items;
        }

        // ============================================================================
        // 加载
        // ============================================================================

        /// <summary>重新加载设备与变量数量；尽量保持选中。</summary>
        /// <remarks>
        /// 还原选中项不是锦上添花：变量页每次增删变量都会 Reload 一次，
        /// 不还原的话每保存一个变量，左侧就自己跳回第一台设备。
        /// 原设备已不存在时才落到首项。
        /// </remarks>
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

            // 优先还原刷新前的选中项
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

        /// <summary>列表选中项变化：记下 Id 并通知页面。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void ListDevices_SelectionChanged (object sender, SelectionChangedEventArgs e) {
            var row = listDevices.SelectedItem as Row;
            _selectedId = row != null ? row.Id : null;
            if (DeviceSelected != null)
                DeviceSelected(_selectedId);
        }

        /// <summary>统计某设备下的变量条数，显示在行尾。</summary>
        /// <param name="deviceId">设备 Id。</param>
        /// <returns>变量数；服务未注入或 Id 为空时返回 0。</returns>
        private int CountVars (string deviceId) {
            if (VariableService == null || string.IsNullOrEmpty(deviceId))
                return 0;
            return VariableService.Variables.Count(v => v != null && v.DeviceId == deviceId);
        }

        /// <summary>状态 → 状态点颜色。</summary>
        /// <param name="status">设备状态。</param>
        /// <returns>
        /// 主题画刷；主题资源缺失时回退到一个固定灰色，
        /// 而不是返回 null——null 会让状态点整个消失，比颜色不对更难察觉。
        /// </returns>
        private Brush BrushOf (DeviceStatusType status) {
            string key = "SF.Brush.Text.Secondary";
            if (status == DeviceStatusType.Success) key = "SF.Brush.Status.Success";
            else if (status == DeviceStatusType.Error) key = "SF.Brush.Status.Error";
            else if (status == DeviceStatusType.Warning || status == DeviceStatusType.Connecting)
                key = "SF.Brush.Status.Warning";

            return TryFindResource(key) as Brush
                   ?? new SolidColorBrush(Color.FromRgb(0x9D, 0x9D, 0x9D));
        }

        // ============================================================================
        // 行模型
        // ============================================================================

        /// <summary>一行的展示模型。只读快照，不监听变更——刷新走整表 <see cref="Reload"/>。</summary>
        private sealed class Row {

            /// <summary>设备 Id，选中时回传给页面。</summary>
            public string Id { get; set; }

            /// <summary>主标题。设备没起名时退回显示 Id，不留空行。</summary>
            public string Name { get; set; }

            /// <summary>副标题，显示型号。</summary>
            public string SubTitle { get; set; }

            /// <summary>行尾的「N 个变量」文案。</summary>
            public string VariableCountText { get; set; }

            /// <summary>状态点颜色。</summary>
            public Brush StatusBrush { get; set; }

            /// <summary>由设备信息构建一行。</summary>
            /// <param name="d">设备。</param>
            /// <param name="count">该设备下的变量数。</param>
            /// <param name="brush">状态取色委托，由外层控件提供（需要访问主题资源）。</param>
            /// <returns>展示行。</returns>
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
