// 文件: Services/LocalVariableStore.cs
// 层级: UI层 — 服务实现
// 作用: IVariableService 的内存实现，变量列表仅存储在内存中（当前版本不持久化）。
//       WriteAsync 根据变量的 DataType 将值序列化为字节数组，然后调用 gRPC WriteAsync。
//       字节序均使用大端（Big-Endian），与 PLC 通信规范一致。

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.UI.Wpf.Core.Interfaces;
using CommunicationKernel.UI.Wpf.Core.Enums;
using CommunicationKernel.UI.Wpf.Core.Models;

namespace CommunicationKernel.UI.Wpf.Services
{
    /// <summary>
    /// <see cref="IVariableService"/> 的内存实现。
    /// 变量列表存储在 List&lt;VariableItem&gt; 中，写入时通过 <see cref="EngineHostGrpcClient"/> 发送。
    /// 每次 Add / Update / Remove 后触发 <see cref="VariablesChanged"/> 事件，
    /// 供 <c>VariablePollingService</c> 同步轮询任务集合。
    /// </summary>
    public sealed class LocalVariableStore : IVariableService
    {
        // -------------------------------------------------------------------------
        // 私有字段
        // -------------------------------------------------------------------------

        /// <summary>gRPC 客户端，用于执行 WriteAsync。</summary>
        private readonly EngineHostGrpcClient _client;

        /// <summary>内存变量列表，所有 CRUD 操作均在此列表上进行。</summary>
        private readonly List<VariableItem> _items = new List<VariableItem>();

        /// <summary>保护 _items 列表的同步锁（支持多线程访问）。</summary>
        private readonly object _lock = new object();

        // -------------------------------------------------------------------------
        // IVariableService — 事件
        // -------------------------------------------------------------------------

        /// <summary>
        /// 变量列表发生变化（Add / Update / Remove）时触发。
        /// 触发线程与调用方一致（通常是 UI 线程或后台线程均有可能）。
        /// </summary>
        public event Action VariablesChanged;

        // -------------------------------------------------------------------------
        // 构造函数
        // -------------------------------------------------------------------------

        /// <summary>
        /// 初始化 LocalVariableStore。
        /// </summary>
        /// <param name="client">已初始化的 gRPC 客户端，用于写入操作。</param>
        public LocalVariableStore(EngineHostGrpcClient client)
        {
            // 保存 gRPC 客户端引用
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        // -------------------------------------------------------------------------
        // IVariableService 实现
        // -------------------------------------------------------------------------

        /// <summary>
        /// 返回当前变量列表的只读快照。
        /// 每次调用返回新的数组，调用方对其修改不影响内部列表。
        /// </summary>
        public IReadOnlyList<VariableItem> Variables
        {
            get
            {
                lock (_lock)
                {
                    // 返回内部列表的副本以保证线程安全
                    return _items.ToArray();
                }
            }
        }

        /// <summary>
        /// 添加新变量到内存列表。
        /// 若 item.Id 为空或 null，自动生成新 Guid。
        /// </summary>
        /// <param name="item">要添加的变量定义。</param>
        public void Add(VariableItem item)
        {
            if (item == null)
                throw new ArgumentNullException(nameof(item));

            // 若 Id 未设置，自动生成
            if (string.IsNullOrWhiteSpace(item.Id))
                item.Id = Guid.NewGuid().ToString();

            lock (_lock)
            {
                // 追加到列表末尾
                _items.Add(item);
            }

            // 通知轮询服务同步轮询任务集合
            VariablesChanged?.Invoke();
        }

        /// <summary>
        /// 更新已有变量的定义。
        /// 根据 item.Id 在列表中查找并替换，未找到则忽略（幂等）。
        /// </summary>
        /// <param name="item">已修改的变量定义。</param>
        public void Update(VariableItem item)
        {
            if (item == null)
                throw new ArgumentNullException(nameof(item));

            lock (_lock)
            {
                // 查找列表中 Id 匹配的条目
                for (int i = 0; i < _items.Count; i++)
                {
                    if (_items[i].Id == item.Id)
                    {
                        // 找到则替换
                        _items[i] = item;
                        return;
                    }
                }
                // 未找到，忽略（不抛出异常，保持幂等）
            }

            // 通知轮询服务同步轮询任务集合（Update 可能修改了 IsPollingEnabled / ScanRateMs）
            VariablesChanged?.Invoke();
        }

        /// <summary>
        /// 从内存列表移除指定 Id 的变量。
        /// 未找到则忽略（幂等）。
        /// </summary>
        /// <param name="id">要移除的变量 Id。</param>
        public void Remove(string id)
        {
            lock (_lock)
            {
                // 查找并移除匹配条目
                for (int i = 0; i < _items.Count; i++)
                {
                    if (_items[i].Id == id)
                    {
                        // 找到则移除
                        _items.RemoveAt(i);
                        return;
                    }
                }
                // 未找到，忽略
            }

            // 通知轮询服务停止被删除变量的轮询任务
            VariablesChanged?.Invoke();
        }

        /// <summary>
        /// 向 PLC 写入指定变量的值。
        /// 根据变量的 DataType 将 value 序列化为字节数组（大端序），
        /// 然后通过 gRPC WriteAsync 发送到 EngineHost。
        /// </summary>
        /// <param name="id">目标变量的 Id。</param>
        /// <param name="value">要写入的值，类型应与变量 DataType 匹配。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>操作结果，Success = true 表示写入成功。</returns>
        public async Task<OperationResult> WriteAsync(string id, object value, CancellationToken ct)
        {
            // 查找目标变量
            VariableItem variable = null;
            lock (_lock)
            {
                foreach (VariableItem v in _items)
                {
                    if (v.Id == id)
                    {
                        // 找到目标变量
                        variable = v;
                        break;
                    }
                }
            }

            // 变量不存在，返回失败
            if (variable == null)
                return OperationResult.Fail("NOT_FOUND", string.Format("变量 {0} 不存在", id));

            // 根据 DataType 将 value 序列化为字节数组
            byte[] bytes;
            try
            {
                bytes = SerializeValue(variable.DataType, value);
            }
            catch (Exception ex)
            {
                // 序列化失败，返回解析错误
                return OperationResult.Fail("PARSE_ERROR", ex.Message);
            }

            // 调用 gRPC WriteAsync 将字节数组写入 PLC
            WriteResultDto result = await _client.WriteAsync(
                variable.DeviceId,
                variable.Address,
                bytes,
                ct).ConfigureAwait(false);

            if (result.Success)
            {
                // 写入成功，返回 Ok
                return OperationResult.Ok();
            }
            else
            {
                // gRPC 服务端返回失败，携带错误码和描述
                return OperationResult.Fail(result.ErrorCode, result.ErrorMessage);
            }
        }

        // -------------------------------------------------------------------------
        // 私有辅助方法
        // -------------------------------------------------------------------------

        /// <summary>
        /// 根据 DataType 枚举将值对象序列化为字节数组（大端序）。
        /// </summary>
        /// <param name="dataType">变量的数据类型枚举值。</param>
        /// <param name="value">待序列化的值。</param>
        /// <returns>序列化后的字节数组（大端序）。</returns>
        /// <exception cref="InvalidOperationException">不支持的 DataType 或值无法转换。</exception>
        private static byte[] SerializeValue(VariableDataType dataType, object value)
        {
            switch (dataType)
            {
                case VariableDataType.Bool:
                {
                    // bool: 1 字节，true = 0x01，false = 0x00
                    bool b = Convert.ToBoolean(value);
                    return new byte[] { b ? (byte)0x01 : (byte)0x00 };
                }
                case VariableDataType.Int16:
                {
                    // int16: 2 字节大端序
                    short s = Convert.ToInt16(value);
                    return new byte[] { (byte)(s >> 8), (byte)(s & 0xFF) };
                }
                case VariableDataType.UInt16:
                {
                    ushort u = Convert.ToUInt16(value);
                    return new byte[] { (byte)(u >> 8), (byte)(u & 0xFF) };
                }
                case VariableDataType.Int32:
                {
                    // int32: 4 字节大端序
                    int n = Convert.ToInt32(value);
                    return new byte[] {
                        (byte)((n >> 24) & 0xFF), (byte)((n >> 16) & 0xFF),
                        (byte)((n >> 8)  & 0xFF), (byte)(n & 0xFF)
                    };
                }
                case VariableDataType.UInt32:
                {
                    uint u = Convert.ToUInt32(value);
                    return new byte[] {
                        (byte)((u >> 24) & 0xFF), (byte)((u >> 16) & 0xFF),
                        (byte)((u >> 8)  & 0xFF), (byte)(u & 0xFF)
                    };
                }
                case VariableDataType.Int64:
                {
                    long l = Convert.ToInt64(value);
                    byte[] buf = new byte[8];
                    for (int i = 7; i >= 0; i--) { buf[i] = (byte)(l & 0xFF); l >>= 8; }
                    // 翻转为大端序
                    Array.Reverse(buf);
                    return buf;
                }
                case VariableDataType.UInt64:
                {
                    ulong u = Convert.ToUInt64(value);
                    byte[] buf = new byte[8];
                    for (int i = 7; i >= 0; i--) { buf[i] = (byte)(u & 0xFF); u >>= 8; }
                    Array.Reverse(buf);
                    return buf;
                }
                case VariableDataType.Float:
                {
                    // float: 4 字节 IEEE 754 大端序
                    float f = Convert.ToSingle(value);
                    byte[] le = BitConverter.GetBytes(f); // .NET 返回小端序
                    return new byte[] { le[3], le[2], le[1], le[0] };
                }
                case VariableDataType.Double:
                {
                    double d = Convert.ToDouble(value);
                    byte[] le = BitConverter.GetBytes(d);
                    Array.Reverse(le);
                    return le;
                }
                case VariableDataType.String:
                {
                    // string: UTF-8 编码字节
                    string str = value != null ? value.ToString() : string.Empty;
                    return Encoding.UTF8.GetBytes(str);
                }
                default:
                    // 不支持的数据类型，抛出异常由调用方捕获并转为 PARSE_ERROR
                    throw new InvalidOperationException(
                        string.Format("不支持的数据类型: {0}", dataType));
            }
        }
    }
}
