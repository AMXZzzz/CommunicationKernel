using System.Collections.Generic;
using CommunicationDebuggingTools.Core.Models;

namespace CommunicationDebuggingTools.Core.Interfaces {
    /// <summary>
    /// 设备配置的持久化契约。
    /// 具体存储介质（JSON 文件、数据库等）由 Business/Infrastructure 层实现，
    /// 上层（业务服务、UI）只依赖本接口，从而实现存储介质可替换。
    /// </summary>
    public interface IDeviceRepository {
        /// <summary>
        /// 加载所有已保存的设备配置。
        /// </summary>
        /// <returns>设备列表；若尚无数据应返回空列表而非 null。</returns>
        IList<DeviceInfo> LoadAll ();

        /// <summary>
        /// 将当前全部设备配置写入持久化存储（全量覆盖）。
        /// </summary>
        /// <param name="devices">待保存的设备列表。</param>
        void SaveAll (IList<DeviceInfo> devices);
    }
}