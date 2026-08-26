// -----------------------------------------------------------------------------
// 文件: Services/WebVariableStore.cs
// 层级: UI 层 — Blazor Server
// 作用: 持久化变量定义。变量是上位机配置，不属于 Host.App。
// -----------------------------------------------------------------------------

using CommunicationKernel.Host.Sdk;
using System.Text.Json;

namespace CommunicationKernel.UI.WebMaster.Services;

/// <summary>一条本地变量定义。当前值不落盘。</summary>
public sealed class WebVariable
{
    /// <summary>稳定标识，增删改与轮询记账都用它；构造即生成，导入时可能被重新分配。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>面向操作员的变量名，例如「传送带速度」。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>所属路由 Id，决定这条变量读写哪台设备。</summary>
    public string RouteId { get; set; } = string.Empty;

    /// <summary>
    /// 设备地址，例如 <c>40001</c> / <c>DB1.DBW0</c> / <c>DT100</c>。
    /// </summary>
    /// <remarks>
    /// 对 UI 是<b>不透明字符串</b>，原样下发给协议插件解释。
    /// 本层不得解析其格式——地址语法属于协议知识，只能存在于插件 DLL 内。
    /// </remarks>
    public string Address { get; set; } = string.Empty;

    /// <summary>Bool / Int16 / UInt16 / Int32 / UInt32 / Float / Hex。</summary>
    public string DataType { get; set; } = "Int16";

    /// <summary>读取字节数。定长类型由类型决定，String / Hex 需按现场实际区域大小填。</summary>
    public int Length { get; set; } = 2;

    /// <summary>是否纳入后台轮询。会落盘——重启浏览器后应当继续轮询。</summary>
    public bool Polling { get; set; }

    /// <summary>轮询周期（毫秒）。实际生效值不低于 200ms。</summary>
    public int ScanRateMs { get; set; } = 1000;

    /// <summary>工程单位，例如「rpm」「℃」；纯展示用，不参与编解码。</summary>
    public string Unit { get; set; } = string.Empty;

    // ------------------------------------------------------------------------
    // 以下为运行期状态，一律 JsonIgnore：它们是"此刻读到什么"而非配置。
    // 落盘会把磁盘写穿（轮询每秒更新若干次），
    // 且重启后显示上一次的陈旧读数比显示 "--" 更危险——
    // 操作员会以为那是当前值。
    // ------------------------------------------------------------------------

    /// <summary>已解码的当前值；失败时是错误码。初始 "--" 表示尚未读取。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayValue { get; set; } = "--";

    /// <summary>行内写入框的文本，由表格双向绑定。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string WriteText { get; set; } = string.Empty;

    /// <summary>最近一次读取是否失败，决定该行是否标红。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsError { get; set; }

    /// <summary>最近一次读取时刻（HH:mm:ss），作为值单元格的 tooltip。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string LastUpdated { get; set; } = string.Empty;
}

/// <summary>变量表磁盘镜像。线程安全。</summary>
/// <remarks>
/// 单例。写入方是 Blazor 渲染线程（增删改）与后台轮询线程（回填读值），
/// 读取方是各页面的渲染，因此全部操作都在锁内完成。
/// </remarks>
public sealed class WebVariableStore
{
    /// <summary>导入导出用的 JSON 选项；落盘选项在 <see cref="JsonFileStore"/> 中统一定义。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>保护 <see cref="_items"/> 与落盘动作的互斥锁。</summary>
    private readonly object _lock = new();

    /// <summary>
    /// 变量表。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="List{T}"/> 而非字典：变量表要保持<b>录入顺序</b>——
    /// 操作员是按产线工位的顺序录的，重排会让人在表格里找不到熟悉的位置。
    /// 按 Id 查找是低频操作，线性扫描在几百条的规模下完全够用。
    /// </remarks>
    private readonly List<WebVariable> _items = new();

    /// <summary>应用日志。</summary>
    private readonly AppLogStore _log;

    /// <summary>
    /// 表内容或读值变化时触发。
    /// </summary>
    /// <remarks>
    /// 可能在<b>后台线程</b>上触发（轮询回填），订阅方必须 <c>InvokeAsync</c> 回 UI 线程。
    /// 事件一律在锁<b>外</b>触发：订阅方的处理可能同步回调进本类，在锁内触发会死锁。
    /// </remarks>
    public event Action? Changed;

    /// <param name="log">应用日志，用于上报载入与落盘失败。</param>
    public WebVariableStore(AppLogStore log)
    {
        _log = log;
        Load();
    }

    /// <summary>取变量表快照。</summary>
    /// <remarks>
    /// 返回列表副本，但元素本身<b>不</b>克隆——表格要能通过双向绑定改 <c>WriteText</c>，
    /// 克隆会让输入框里打的字丢失。代价是调用方能改到内部对象，
    /// 因此约定：改动必须经 <see cref="Update"/> 才会落盘。
    /// </remarks>
    public IReadOnlyList<WebVariable> GetAll()
    {
        lock (_lock)
            return _items.ToList();
    }

    /// <summary>追加一条变量并落盘。</summary>
    /// <param name="item">变量定义；<c>Id</c> 为空时自动生成。</param>
    public void Add(WebVariable item)
    {
        ArgumentNullException.ThrowIfNull(item);

        lock (_lock)
        {
            // 导入来的条目可能没有 Id；没有 Id 就无法被更新或删除
            if (string.IsNullOrWhiteSpace(item.Id))
                item.Id = Guid.NewGuid().ToString("N");

            _items.Add(item);
            Persist_NoLock();
        }

        Changed?.Invoke();
    }

    /// <summary>按 Id 覆盖一条变量并落盘。Id 不存在时静默返回。</summary>
    /// <remarks>
    /// 找不到即返回而不新增：调用方是编辑表单，
    /// 目标不存在说明它在表单打开期间被删了，此时静默放弃比复活它更合理。
    /// </remarks>
    public void Update(WebVariable item)
    {
        ArgumentNullException.ThrowIfNull(item);

        lock (_lock)
        {
            int i = _items.FindIndex(v => v.Id == item.Id);
            if (i < 0) return;

            // 按下标替换而非先删后插，保持录入顺序不变
            _items[i] = item;
            Persist_NoLock();
        }

        Changed?.Invoke();
    }

    /// <summary>按 Id 删除变量并落盘。</summary>
    public void Remove(string id)
    {
        lock (_lock)
        {
            _items.RemoveAll(v => v.Id == id);
            Persist_NoLock();
        }

        Changed?.Invoke();
    }

    /// <summary>整表替换并落盘，用于「导入并覆盖」。</summary>
    public void ReplaceAll(IEnumerable<WebVariable> items)
    {
        lock (_lock)
        {
            _items.Clear();
            _items.AddRange(items);
            Persist_NoLock();
        }

        Changed?.Invoke();
    }

    /// <summary>只更新内存中的读值，不写盘。</summary>
    /// <param name="id">目标变量 Id；不存在时静默返回。</param>
    /// <param name="display">已解码的显示值，或失败时的错误码。</param>
    /// <param name="error">是否为错误状态，决定该行是否标红。</param>
    /// <remarks>
    /// <b>刻意不落盘。</b>读值是运行期数据不是配置，
    /// 而轮询每秒会调用本方法若干次——落盘会把磁盘写穿，
    /// 且重启后显示上一次的陈旧读数比显示 "--" 更危险。
    /// 对应地，<see cref="WebVariable"/> 的这三个字段都标了 <c>JsonIgnore</c>。
    /// </remarks>
    public void ApplyRead(string id, string display, bool error)
    {
        lock (_lock)
        {
            WebVariable? v = _items.FirstOrDefault(x => x.Id == id);
            if (v is null) return;

            v.DisplayValue = display;
            v.IsError = error;

            // 只记时分秒：表格那一列是 tooltip，日期对"这个值是不是刚读的"没有帮助
            v.LastUpdated = DateTime.Now.ToString("HH:mm:ss");
        }

        Changed?.Invoke();
    }

    /// <summary>把变量表序列化为 JSON 文本，供操作员复制或另存。</summary>
    public string ExportJson()
    {
        lock (_lock)
            return JsonSerializer.Serialize(_items, JsonOptions);
    }

    /// <summary>
    /// 从 JSON 文本导入变量。
    /// </summary>
    /// <param name="json">变量表 JSON，格式与 <see cref="ExportJson"/> 的输出一致。</param>
    /// <param name="replace">true 整表替换，false 追加到现有表尾。</param>
    /// <returns>成功导入的条数；内容为字面量 null 时返回 0。</returns>
    /// <exception cref="JsonException">JSON 语法错误，由调用方转成界面提示。</exception>
    /// <remarks>
    /// <para>
    /// <b>Id 冲突处理是这里的关键。</b>追加模式下，若导入文件与现有表存在相同 Id
    /// （常见于「先导出、改几条、再导回来」），不处理会让两条不同的变量共用一个 Id——
    /// 此后更新和删除都会命中错误的那条，而且完全没有报错。
    /// 因此冲突项一律重新分配 Id，宁可产生重复定义也不能破坏标识唯一性。
    /// </para>
    /// <para>
    /// 反序列化刻意放在锁<b>外</b>：解析可能耗时（大文件），
    /// 在锁内做会阻塞轮询线程回填读值，界面表现为导入期间数据卡住。
    /// </para>
    /// </remarks>
    public int ImportJson(string json, bool replace)
    {
        List<WebVariable>? list = JsonSerializer.Deserialize<List<WebVariable>>(json, JsonOptions);

        // 文件内容是字面量 null：不是错误，只是没有内容
        if (list is null) return 0;

        foreach (WebVariable v in list)
        {
            // 手工编辑过的文件可能没有 Id
            if (string.IsNullOrWhiteSpace(v.Id))
                v.Id = Guid.NewGuid().ToString("N");

            // 导入的是定义不是读数：清掉文件里可能残留的运行期状态，
            // 否则会显示一个来自别处、且永远不会刷新的假值
            v.DisplayValue = "--";
            v.IsError = false;
        }

        lock (_lock)
        {
            if (replace)
            {
                _items.Clear();
            }
            else
            {
                // 追加模式：与现有表撞 Id 的一律换新 Id，保证标识唯一
                HashSet<string> existing = new(_items.Select(x => x.Id), StringComparer.Ordinal);
                foreach (WebVariable v in list)
                {
                    if (existing.Contains(v.Id))
                        v.Id = Guid.NewGuid().ToString("N");
                }
            }

            _items.AddRange(list);
            Persist_NoLock();
        }

        Changed?.Invoke();
        return list.Count;
    }

    private void Load()
    {
        // 读写都走 Host.Sdk 的 JsonFileStore：与 WPF 端共用同一套原子落盘实现
        List<WebVariable> list = JsonFileStore.Load<WebVariable>(
            WebPaths.VariablesFile, out string error);

        if (error is not null)
        {
            _log.Warn("Variables", "载入 web-variables.json 失败: " + error);
            return;
        }

        lock (_lock)
        {
            _items.Clear();
            _items.AddRange(list);
        }

        _log.Info("Variables", "已载入 " + _items.Count + " 条变量");
    }

    /// <summary>
    /// 写回磁盘。调用方必须已持有 <see cref="_lock"/>。
    /// </summary>
    /// <remarks>
    /// 此前是直接 File.WriteAllText 覆写，写入中途掉电会留下截断的 JSON。
    /// 现改为经 JsonFileStore 原子替换。
    /// </remarks>
    private void Persist_NoLock()
    {
        if (!JsonFileStore.Save(WebPaths.VariablesFile, _items, out string error))
            _log.Warn("Variables", "写入 web-variables.json 失败: " + error);
    }
}
