using CommunicationDebuggingTools.Core.Models;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace CommunicationDebuggingTools.Core.Interfaces {
    /// <summary>
    /// 设备业务服务契约：UI 层只依赖此接口，不直接接触持久化与协议实现细节。
    /// 负责设备列表的增删改查、持久化读写以及连接/断开协议的调度。
    /// </summary>
    public interface IDeviceService {
        /// <summary>
        /// 当前已加载的设备集合（可直接绑定到 UI，增删改时自动通知界面刷新）。
        /// </summary>
        ObservableCollection<DeviceInfo> Devices { get; }

        /// <summary>
        /// 从持久化存储重新加载设备列表到 <see cref="Devices"/>。
        /// </summary>
        void Load ();

        /// <summary>
        /// 将当前 <see cref="Devices"/> 列表持久化到存储。
        /// </summary>
        void Save ();

        /// <summary>
        /// 新增一个设备，并自动持久化。
        /// </summary>
        /// <param name="device">新设备信息。</param>
        void Add (DeviceInfo device);

        /// <summary>
        /// 根据 <see cref="DeviceInfo.Id"/> 更新已有设备的配置，并自动持久化。
        /// </summary>
        /// <param name="device">包含新值的设备信息。</param>
        void Update (DeviceInfo device);

        /// <summary>
        /// 根据设备 Id 删除对应设备，并自动持久化。
        /// </summary>
        /// <param name="id">设备唯一标识。</param>
        void Remove (string id);

        /// <summary>
        /// 建立与指定设备的通信连接（具体协议由对应插件实现）。
        /// </summary>
        /// <param name="id">设备唯一标识。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>连接是否成功。</returns>
        Task<bool> ConnectAsync (string id, CancellationToken cancellationToken);

        /// <summary>
        /// 断开与指定设备的通信连接。
        /// </summary>
        /// <param name="id">设备唯一标识。</param>
        void Disconnect (string id);

        /// <summary>获取该设备当前协议实例（未连接可为 null）。</summary>
        /// <param name="deviceId">设备唯一标识。</param>
        IProtocol GetProtocol (string deviceId);

        /// <summary>
        /// 一次性断开所有设备的通信连接（用于应用退出时清理资源）。
        /// </summary>
        void DisconnectAll ();

        /// <summary>
        /// 检查所有已连接会话是否仍然存活，将已断线的设备标为离线。
        /// 必须在 UI 线程（如 DispatcherTimer 回调）中调用，避免跨线程更新绑定属性。
        /// </summary>
        void CheckConnections ();

        void ReportCommSuccess (string deviceId);
        void ReportCommError (string deviceId);

    }
}