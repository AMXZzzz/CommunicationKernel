#nullable disable

// -----------------------------------------------------------------------------
// 文件: Services/DeviceConfigStore.cs
// 层级: UI层 — 服务实现
// 作用: 把设备配置持久化到磁盘，作为上位机侧的唯一事实来源。
//
// 为什么需要它：
//   EngineHost 的路由是纯内存对象，进程重启即全部丢失。此前上位机没有任何
//   本地留存，于是宿主一重启：
//     · Load() 发现服务端没有这些路由，把本地设备列表整个清空；
//     · 已配置的变量继续对着不存在的路由轮询，永远收到 RouteNotFound；
//     · 操作员只能把每台设备重新手工录一遍。
//   业务配置属于上位机（宿主只是无状态的通讯引擎），因此配置必须落在这里，
//   并由 IRouteReconciler 据此把路由重新推给宿主。
//
// 存储位置沿用 settings.json / protocols.cache.json 的约定：
//   %APPDATA%\CommunicationKernel\devices.json
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CommunicationKernel.UI.Wpf.Core.Logging;
using CommunicationKernel.UI.Wpf.Core.Models;

namespace CommunicationKernel.UI.Wpf.Services
{
    /// <summary>
    /// 设备配置的磁盘持久化。线程安全：所有公开方法内部加锁。
    /// </summary>
    public sealed class DeviceConfigStore
    {
        // -------------------------------------------------------------------------
        // 常量与字段
        // -------------------------------------------------------------------------

        /// <summary>配置文件完整路径。</summary>
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CommunicationKernel", "devices.json");

        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        private readonly object _lock = new object();
        private readonly IAppLogger _log;

        /// <summary>
        /// 内存副本，Key = RouteId。磁盘只是它的镜像，
        /// 读路径一律走内存，避免每次查名称都碰一次文件系统。
        /// </summary>
        private readonly Dictionary<string, DeviceRecord> _records
            = new Dictionary<string, DeviceRecord>(StringComparer.OrdinalIgnoreCase);

        // -------------------------------------------------------------------------
        // 记录类型
        // -------------------------------------------------------------------------

        /// <summary>
        /// 一台设备的可持久化配置。
        /// </summary>
        /// <remarks>
        /// 只存"重新注册这条路由所需的参数"加上"gRPC 不传输的本地元数据"。
        /// IsConnected / StatusType 这类运行期状态一律不存——
        /// 存了会在下次启动时显示成一个从未验证过的连接状态，比不显示更糟。
        /// </remarks>
        public sealed class DeviceRecord
        {
            // 注册路由所需
            public string Id            { get; set; }
            public string Protocol      { get; set; }
            public string TransportKind { get; set; }
            public string Ip            { get; set; }
            public int    Port          { get; set; }
            public string Station       { get; set; }
            public int    StationNo     { get; set; }
            public string SerialPort    { get; set; }
            public int    BaudRate      { get; set; }

            // gRPC 路由模型里没有、必须本地留存的展示元数据
            public string Name              { get; set; }
            public string Model             { get; set; }
            public bool   IsDualLane        { get; set; }
            public string Lane              { get; set; }
            public string ExtraSettingsJson { get; set; }
        }

        // -------------------------------------------------------------------------
        // 构造
        // -------------------------------------------------------------------------

        /// <summary>构造并立即从磁盘载入既有配置。</summary>
        /// <param name="log">可选日志记录器。</param>
        public DeviceConfigStore(IAppLogger log = null)
        {
            _log = log;
            LoadFromDisk();
        }

        // -------------------------------------------------------------------------
        // 公开方法
        // -------------------------------------------------------------------------

        /// <summary>返回全部已持久化的设备配置快照。</summary>
        public IReadOnlyList<DeviceRecord> GetAll()
        {
            lock (_lock)
            {
                return new List<DeviceRecord>(_records.Values);
            }
        }

        /// <summary>按路由 ID 取配置；不存在返回 null。</summary>
        public DeviceRecord Get(string routeId)
        {
            if (string.IsNullOrWhiteSpace(routeId)) return null;

            lock (_lock)
            {
                return _records.TryGetValue(routeId, out DeviceRecord record) ? record : null;
            }
        }

        /// <summary>写入（或覆盖）一台设备的配置并落盘。</summary>
        public void Save(string routeId, DeviceInfo info)
        {
            if (string.IsNullOrWhiteSpace(routeId) || info == null) return;

            lock (_lock)
            {
                _records[routeId] = new DeviceRecord
                {
                    Id                = routeId,
                    Protocol          = info.Protocol,
                    TransportKind     = info.TransportKind,
                    Ip                = info.Ip,
                    Port              = info.Port,
                    Station           = info.Station,
                    StationNo         = info.StationNo,
                    SerialPort        = info.SerialPort,
                    BaudRate          = info.BaudRate,
                    Name              = info.Name,
                    Model             = info.Model,
                    IsDualLane        = info.IsDualLane,
                    Lane              = info.Lane,
                    ExtraSettingsJson = info.ExtraSettingsJson
                };

                FlushToDisk();
            }
        }

        /// <summary>删除一台设备的配置并落盘。</summary>
        public void Delete(string routeId)
        {
            if (string.IsNullOrWhiteSpace(routeId)) return;

            lock (_lock)
            {
                if (_records.Remove(routeId))
                    FlushToDisk();
            }
        }

        // -------------------------------------------------------------------------
        // 磁盘 I/O
        // -------------------------------------------------------------------------

        /// <summary>从磁盘载入；文件缺失或损坏时以空配置起步，不抛异常。</summary>
        private void LoadFromDisk()
        {
            try
            {
                if (!File.Exists(FilePath)) return;

                string json = File.ReadAllText(FilePath);
                List<DeviceRecord> loaded =
                    JsonSerializer.Deserialize<List<DeviceRecord>>(json, SerializerOptions);

                if (loaded == null) return;

                foreach (DeviceRecord record in loaded)
                {
                    if (record != null && !string.IsNullOrWhiteSpace(record.Id))
                        _records[record.Id] = record;
                }

                _log?.Info("Device", string.Format("已从本地载入 {0} 台设备配置", _records.Count));
            }
            catch (Exception ex)
            {
                // 配置损坏不应阻止程序启动：以空配置继续，用户重新录入即可覆盖。
                // 这里刻意不删除损坏文件——留着便于事后排查。
                _log?.Error("Device", "载入本地设备配置失败，将以空配置启动", ex);
            }
        }

        /// <summary>
        /// 写回磁盘。调用方必须已持有 <see cref="_lock"/>。
        /// </summary>
        /// <remarks>
        /// 先写临时文件再替换：直接覆盖时若在写入中途断电或崩溃，
        /// 会留下一个被截断的 JSON，下次启动直接丢失全部设备配置。
        /// </remarks>
        private void FlushToDisk()
        {
            try
            {
                string directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                string json = JsonSerializer.Serialize(
                    new List<DeviceRecord>(_records.Values), SerializerOptions);

                string tempPath = FilePath + ".tmp";
                File.WriteAllText(tempPath, json);

                // 分支1：目标已存在——用 Replace 做原子替换
                if (File.Exists(FilePath))
                    File.Replace(tempPath, FilePath, null);
                else
                    File.Move(tempPath, FilePath);
            }
            catch (Exception ex)
            {
                // 落盘失败只影响下次启动的恢复能力，不应中断当前操作
                _log?.Error("Device", "保存本地设备配置失败", ex);
            }
        }
    }
}
