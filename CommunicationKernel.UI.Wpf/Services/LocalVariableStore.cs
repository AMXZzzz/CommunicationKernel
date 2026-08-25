#nullable disable

// -----------------------------------------------------------------------------
// 文件: Services/LocalVariableStore.cs
// 层级: UI 层 — WPF 服务实现
// 作用: IVariableService 的内存+磁盘实现；CRUD 后触发轮询重建，写入经 gRPC WriteAsync。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Host.Sdk;
using CommunicationKernel.UI.Wpf.Core.Enums;
using CommunicationKernel.UI.Wpf.Core.Interfaces;
using CommunicationKernel.UI.Wpf.Core.Models;

namespace CommunicationKernel.UI.Wpf.Services
{
    /// <summary>
    /// <see cref="IVariableService"/> 的内存+磁盘实现。
    /// 变量列表存储在 List&lt;VariableItem&gt; 中，写入时通过 <see cref="HostClient"/> 发送。
    /// 每次 Add / Update / Remove 后触发 <see cref="VariablesChanged"/> 事件，
    /// 供 <c>VariablePollingService</c> 同步轮询任务集合；同时异步持久化到本地 JSON 文件。
    /// </summary>
    public sealed class LocalVariableStore : IVariableService
    {
        // ============================================================================
        // 持久化路径
        // ============================================================================

        /// <summary>变量定义持久化文件路径。本端独立目录，见 <see cref="WpfPaths"/>。</summary>
        private static readonly string PersistPath = WpfPaths.VariablesFile;

        /// <summary>JSON 序列化选项：枚举写字符串，缩进输出便于调试查看。</summary>
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions {
            WriteIndented    = true,
            Converters       = { new JsonStringEnumConverter() },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // ============================================================================
        // 私有字段
        // ============================================================================

        /// <summary>gRPC 客户端，用于执行 WriteAsync。</summary>
        private readonly HostClient _client;

        /// <summary>内存变量列表，所有 CRUD 操作均在此列表上进行。</summary>
        private readonly List<VariableItem> _items = new List<VariableItem>();

        /// <summary>保护 _items 列表的同步锁（支持多线程访问）。</summary>
        private readonly object _lock = new object();

        // ============================================================================
        // IVariableService — 事件
        // ============================================================================

        /// <summary>
        /// 变量列表发生变化（Add / Update / Remove）时触发。
        /// 触发线程与调用方一致（通常是 UI 线程或后台线程均有可能）。
        /// 回调可能在任意线程触发，订阅方需自行切换线程。
        /// </summary>
        public event Action VariablesChanged;

        // ============================================================================
        // 构造函数
        // ============================================================================

        /// <summary>
        /// 初始化 LocalVariableStore，并从磁盘加载上次保存的变量列表。
        /// </summary>
        /// <param name="client">已初始化的 gRPC 客户端，用于写入操作。</param>
        public LocalVariableStore(HostClient client)
        {
            // gRPC 客户端必填，写入走 WriteAsync
            _client = client ?? throw new ArgumentNullException(nameof(client));
            // 启动时从磁盘恢复，恢复失败则静默忽略（内存列表为空，用户可重新导入）
            LoadFromDisk();
        }

        // ============================================================================
        // IVariableService 实现
        // ============================================================================

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
        /// 添加新变量到内存列表，并持久化到磁盘。
        /// 若 item.Id 为空或 null，自动生成新 Guid。
        /// </summary>
        /// <param name="item">要添加的变量定义。</param>
        public void Add(VariableItem item)
        {
            // 空对象无法入库
            if (item == null)
                throw new ArgumentNullException(nameof(item));

            // 若 Id 未设置，自动生成 Guid
            if (string.IsNullOrWhiteSpace(item.Id))
                item.Id = Guid.NewGuid().ToString();

            lock (_lock)
            {
                _items.Add(item);
            }

            // 通知轮询服务同步轮询任务集合，并将最新列表写入磁盘
            VariablesChanged?.Invoke();
            _ = Task.Run(PersistAsync);
        }

        /// <summary>
        /// 更新已有变量的定义，并持久化到磁盘。
        /// 根据 item.Id 在列表中查找并替换，未找到则忽略（幂等）。
        /// </summary>
        /// <param name="item">已修改的变量定义。</param>
        public void Update(VariableItem item)
        {
            // 空对象无法更新
            if (item == null)
                throw new ArgumentNullException(nameof(item));

            bool found = false;
            lock (_lock)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    if (_items[i].Id == item.Id)
                    {
                        // 整对象替换，可能改了轮询开关或扫描周期
                        _items[i] = item;
                        found = true;
                        break;
                    }
                }
            }

            if (found)
            {
                // 通知轮询服务同步轮询任务集合（Update 可能修改了 IsPollingEnabled / ScanRateMs）
                VariablesChanged?.Invoke();
                _ = Task.Run(PersistAsync);
            }
        }

        /// <summary>
        /// 从内存列表移除指定 Id 的变量，并持久化到磁盘。
        /// 未找到则忽略（幂等）。
        /// </summary>
        /// <param name="id">要移除的变量 Id。</param>
        public void Remove(string id)
        {
            bool found = false;
            lock (_lock)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    if (_items[i].Id == id)
                    {
                        // 按 Id 移除，随后通知轮询停止该任务
                        _items.RemoveAt(i);
                        found = true;
                        break;
                    }
                }
            }

            if (found)
            {
                // 通知轮询服务停止被删除变量的轮询任务
                VariablesChanged?.Invoke();
                _ = Task.Run(PersistAsync);
            }
        }

        /// <summary>
        /// 向 PLC 写入指定变量的值。
        /// 根据变量的 DataType 将 value 序列化为字节数组（大端序），
        /// 然后通过 gRPC WriteAsync 发送到 Host.App。
        /// </summary>
        /// <param name="id">目标变量的 Id。</param>
        /// <param name="value">要写入的值，类型应与变量 DataType 匹配。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>操作结果，Success = true 表示写入成功。</returns>
        public async Task<HostOperationResult> WriteAsync(string id, object value, CancellationToken ct)
        {
            VariableItem variable = null;
            lock (_lock)
            {
                foreach (VariableItem v in _items)
                {
                    if (v.Id == id)
                    {
                        variable = v;
                        break;
                    }
                }
            }

            // 变量已被删除或 Id 错误
            if (variable == null)
                return HostOperationResult.Fail("NOT_FOUND", string.Format("变量 {0} 不存在", id));

            byte[] bytes;
            try
            {
                // 按 DataType 把界面值编成大端字节
                bytes = SerializeValue(variable.DataType, value);
            }
            catch (Exception ex)
            {
                // 类型转换失败（例如把 "abc" 当 Int16）
                return HostOperationResult.Fail("PARSE_ERROR", ex.Message);
            }

            // 经 gRPC 下发到指定路由的地址
            WriteResultDto result = await _client.WriteAsync(
                variable.DeviceId,
                variable.Address,
                bytes,
                ct).ConfigureAwait(false);

            // 直接上抛。WriteResultDto 派生自 HostOperationResult，字段形状完全一致，
            // 无需再拆开重装——此前那次转换纯属把三个字段搬进另一个同形状对象，
            // 除了制造错位机会没有任何收益。
            return result;
        }

        // ============================================================================
        // 持久化
        // ============================================================================

        /// <summary>
        /// 从磁盘加载变量列表。文件不存在或解析失败时静默忽略（内存列表保持为空）。
        /// 在构造函数中调用，不触发 VariablesChanged（避免启动时轮询服务尚未订阅）。
        /// </summary>
        private void LoadFromDisk()
        {
            try
            {
                // 首次启动尚无文件，保持空列表
                if (!File.Exists(PersistPath))
                    return;

                // 读取并反序列化变量定义（含轮询配置）
                string json = File.ReadAllText(PersistPath, Encoding.UTF8);
                List<VariableItem> loaded = JsonSerializer.Deserialize<List<VariableItem>>(json, JsonOpts);
                if (loaded == null || loaded.Count == 0)
                    return;

                lock (_lock)
                {
                    _items.Clear();
                    foreach (VariableItem item in loaded)
                    {
                        // 过滤无效条目（Id 为空时跳过）
                        if (item != null && !string.IsNullOrWhiteSpace(item.Id))
                        {
                            // 清除运行时状态，避免上次崩溃时的错误值显示在界面上
                            item.LastValue = null;
                            item.LastError = null;
                            _items.Add(item);
                        }
                    }
                }
            }
            catch
            {
                // JSON 损坏或版本不兼容时静默忽略，不影响启动流程
            }
        }

        /// <summary>
        /// 将当前内存变量列表异步写入磁盘。
        /// 先写到同目录临时文件，成功后再原子替换，防止写入中断导致文件损坏。
        /// </summary>
        private async Task PersistAsync()
        {
            try
            {
                // 获取当前列表快照，避免持锁时进行 I/O 操作
                VariableItem[] snapshot;
                lock (_lock)
                {
                    snapshot = _items.ToArray();
                }

                string json = JsonSerializer.Serialize(snapshot, JsonOpts);

                // 确保目录存在
                string dir = Path.GetDirectoryName(PersistPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // 先写临时文件，再原子替换，防止部分写入损坏数据
                string tmp = PersistPath + ".tmp";
                await File.WriteAllTextAsync(tmp, json, Encoding.UTF8).ConfigureAwait(false);
                File.Move(tmp, PersistPath, overwrite: true);
            }
            catch
            {
                // 持久化失败时静默处理，不影响正常业务流程
                // 内存中的变量列表仍然有效，下次成功持久化时会覆盖
            }
        }

        // ============================================================================
        // 私有辅助方法
        // ============================================================================

        /// <summary>
        /// 把界面值编成写入设备的字节。
        /// </summary>
        /// <remarks>
        /// 换算本体在 <see cref="ValueCodec"/>——WPF 与 Web 共用同一份。
        /// 这里原本手写了一整套大端移位（约 70 行），与 Web 端各写一份；
        /// Web 那份曾把字节序写反，写 8 进 PLC 变成 2048。
        /// 同类逻辑分成两处、只有一处出错，正是本项目反复栽的跟头。
        /// </remarks>
        /// <exception cref="InvalidOperationException">类型不支持或数值超范围。</exception>
        private static byte[] SerializeValue(VariableDataType dataType, object value)
        {
            // 字节序目前固定大端：WPF 尚未提供按设备配置字节序的界面，
            // 而大端正是三个协议插件统一产出的排列，与原有行为完全一致。
            if (!ValueCodec.TryEncodeValue(
                    value,
                    ValueParser.ToCodecType(dataType),
                    length: 0,
                    out byte[] bytes,
                    out string error,
                    ByteOrder.ABCD))
            {
                throw new InvalidOperationException(error);
            }

            return bytes;
        }
    }
}
