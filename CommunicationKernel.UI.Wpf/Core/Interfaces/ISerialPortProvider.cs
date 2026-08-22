#nullable disable

// -----------------------------------------------------------------------------
// 文件: Core/Interfaces/ISerialPortProvider.cs
// 层级: UI层 — 核心接口
// 作用: 提供「宿主所在机器」上的串口清单，供设备编辑面板渲染下拉框。
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Host.Sdk;

namespace CommunicationKernel.UI.Wpf.Core.Interfaces
{
    /// <summary>
    /// 串口清单提供者。
    /// </summary>
    /// <remarks>
    /// <b>清单来自 Host.App 所在的机器，不是本机。</b>
    /// 宿主部署在树莓派、上位机在办公室 PC 时，本机的 COM1/COM2
    /// 与 PLC 毫无关系——选中后注册必然失败，而错误信息会指向
    /// "打不开 COM1"，把人往完全错误的方向引。
    /// </remarks>
    public interface ISerialPortProvider
    {
        /// <summary>
        /// 拉取宿主机器上当前可用的串口。
        /// </summary>
        /// <returns>
        /// 串口清单；宿主不可达、未装串口插件或现场是纯以太网时返回空列表。
        /// 空列表不是错误，界面应保留手工输入能力。
        /// </returns>
        Task<IReadOnlyList<SerialPortDto>> GetPortsAsync(CancellationToken ct);
    }
}
