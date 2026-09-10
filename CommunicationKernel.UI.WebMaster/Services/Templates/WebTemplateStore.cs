// -----------------------------------------------------------------------------
// 文件: WebTemplateStore.cs
// 层级: UI 层 — Blazor Server
// 作用: 设备功能模板库。只存名称 / 类型 / 备注，地址在变量表按设备填。
// -----------------------------------------------------------------------------

using CommunicationKernel.Hosting.Sdk;
using System.Text.Json;

namespace CommunicationKernel.UI.WebMaster.Services;

/// <summary>模板里的一条功能槽。</summary>
public sealed class WebDeviceTemplateSlot
{
    /// <summary>功能名，变量表按此对齐，例如「启动」。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>数据类型，与 <see cref="ValueCodec"/> 名称一致。</summary>
    public string DataType { get; set; } = "Int16";

    /// <summary>备注。</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>仅 String / Hex 使用；其它类型套用时按类型重算。</summary>
    public int Length { get; set; }

    /// <summary>读写方向，随同步下发到变量。详见 <see cref="VariableAccess"/>。</summary>
    public VariableAccess Access { get; set; } = VariableAccess.ReadWrite;

    /// <summary>小数位，随同步下发到变量。详见 <see cref="VariableScale"/>。</summary>
    /// <remarks>
    /// 和读写方向一样属于设备的固有属性：同一型号的位置寄存器都是一位小数，
    /// 不因装在哪条线上而不同。放在模板里，套一次就全都对。
    /// </remarks>
    public int Decimals { get; set; }

    /// <summary>
    /// 兼容上一版的布尔字段，只用于读入旧配置。
    /// </summary>
    /// <remarks>
    /// 只有 setter：System.Text.Json 反序列化时会用它，序列化时因无 getter 而跳过，
    /// 于是旧文件读得进来、新文件不再写出这个字段，一次读写即完成迁移。
    /// <para>
    /// 不做这层兼容的话，上一版勾过的「只读」会在升级后<b>静默</b>变回可读写——
    /// 测量值又能写了，而没有任何提示。
    /// </para>
    /// </remarks>
    public bool ReadOnly
    {
        set { if (value) Access = VariableAccess.ReadOnly; }
    }
}

/// <summary>一类设备的功能模板。</summary>
public sealed class WebDeviceTemplate
{
    /// <summary>稳定 Id，变量行用它标记来源。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>显示名，例如「变频器」。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 引用的其它模板 Id，按顺序在本模板自有槽位<b>之前</b>展开。
    /// </summary>
    /// <remarks>
    /// 「启停」这类功能几乎每台设备都有，逐个模板重抄一遍既费事又会写岔。
    /// 引用而非复制，是为了让「启停」改一次、所有引用它的模板跟着变——
    /// 复制过来的副本做不到这一点，而现场恰恰会在投产后调整这类公共功能。
    /// <para>
    /// 只存 Id 不存名字：模板改名后引用仍然有效。
    /// </para>
    /// </remarks>
    public List<string> Includes { get; set; } = new();

    /// <summary>本模板<b>自有</b>的功能槽，不含引用来的。</summary>
    /// <remarks>
    /// 展开后的完整清单要用 <see cref="WebTemplateStore.ResolveSlots(string)"/> 取，
    /// 直接读本属性会漏掉所有引用进来的槽位。
    /// </remarks>
    public List<WebDeviceTemplateSlot> Slots { get; set; } = new();
}

/// <summary>展开后的一条功能槽，附带它的来源。</summary>
/// <param name="Slot">生效的槽位定义。</param>
/// <param name="SourceTemplateId">
/// 来源模板 Id；<see cref="string.Empty"/> 表示本模板自有。
/// </param>
/// <param name="SourceTemplateName">来源模板显示名；自有时为空。</param>
/// <remarks>
/// 「自有」判定的是<b>给出生效定义的那一层</b>，不是"名字出现过的那一层"。
/// 自有覆盖了同名的引用项时，来源随之变成自有——否则界面会把一项标成
/// 继承（不可编辑），而它其实就在下表里能改。
/// </remarks>
public sealed record ResolvedTemplateSlot(
    WebDeviceTemplateSlot Slot,
    string SourceTemplateId,
    string SourceTemplateName)
{
    /// <summary>是否为本模板自有（可就地编辑）。</summary>
    public bool IsOwn => SourceTemplateId.Length == 0;
}

/// <summary>模板库磁盘镜像。</summary>
public sealed class WebTemplateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _lock = new();
    private readonly List<WebDeviceTemplate> _items = new();
    private readonly AppLogStore _log;

    public event Action? Changed;

    public WebTemplateStore(AppLogStore log)
    {
        _log = log;
        Load();
    }

    public IReadOnlyList<WebDeviceTemplate> GetAll()
    {
        lock (_lock)
            return _items.Select(Clone).ToList();
    }

    public WebDeviceTemplate? Get(string id)
    {
        lock (_lock)
        {
            WebDeviceTemplate? hit = _items.FirstOrDefault(t => t.Id == id);
            return hit is null ? null : Clone(hit);
        }
    }

    public void Add(WebDeviceTemplate item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(item.Id))
                item.Id = Guid.NewGuid().ToString("N");
            _items.Add(Clone(item));
            Persist_NoLock();
        }
        Changed?.Invoke();
    }

    public void Update(WebDeviceTemplate item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_lock)
        {
            int i = _items.FindIndex(t => t.Id == item.Id);
            if (i < 0) return;
            _items[i] = Clone(item);
            Persist_NoLock();
        }
        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            _items.RemoveAll(t => t.Id == id);

            // 顺带摘掉别人对它的引用。留着的话展开时会遇到一个查不到的 Id：
            // 静默跳过等于模板悄悄少了几个槽位，报错又会让整个模板不可用。
            // 两种都不好，不如在删除时就把引用清干净。
            foreach (WebDeviceTemplate t in _items)
                t.Includes.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));

            Persist_NoLock();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// 展开一个模板的完整功能槽清单：先按顺序展开引用，再接自有槽位。
    /// </summary>
    /// <param name="id">模板 Id。</param>
    /// <returns>去重后的槽位清单；模板不存在时返回空表。</returns>
    /// <remarks>
    /// <para>
    /// <b>同名槽位由自有定义覆盖引用来的。</b>「启停」里已有「启动」，引用它的模板
    /// 又自己写了「启动」时，取<b>自有</b>那个——写在这里就是为了改它，
    /// 与 CSS、配置叠加等所有组合体系一致：更具体的覆盖更通用的。
    /// <para>
    /// 位置仍按首次出现确定：被覆盖的槽位留在原位，只换内容。
    /// 否则改一下类型，这一项就会跳到列表末尾，看起来像被删了又加回来。
    /// </para>
    /// <para>
    /// 反过来「先到先得」曾经试过，问题是界面无法如实说明：合计里那一项
    /// 明明来自引用，图例却标成"自有，可在下表改"——而在下表改它根本不生效。
    /// </para>
    /// </para>
    /// <para>
    /// <b>环引用会被截断而不是抛异常。</b> A 引用 B、B 又引用 A 时，
    /// 第二次遇到已在展开路径上的模板就停下并记一条警告。
    /// 抛异常会让整个模板页打不开——配置写坏不该导致界面不可用。
    /// 正常情况下写不出环（<see cref="WouldCreateCycle"/> 在保存前就挡住了），
    /// 这里防的是手工编辑 JSON 或导入外部文件。
    /// </para>
    /// </remarks>
    public IReadOnlyList<WebDeviceTemplateSlot> ResolveSlots(string id)
        => ResolveDetailed(id).Select(r => r.Slot).ToList();

    /// <summary>
    /// 与 <see cref="ResolveSlots"/> 相同，但额外给出每一项的来源模板。
    /// </summary>
    /// <remarks>
    /// 界面需要按行标出「这一项是自有还是从谁那儿来的」——
    /// 只有自有项能就地编辑，继承项要到来源模板里改。
    /// 不给来源的话，操作员对着一张混合表无从判断哪些能动。
    /// <para>
    /// 同步到变量表只关心槽位本身，因此 <see cref="ResolveSlots"/> 保留为
    /// 简单投影；两者共用同一次展开，规则不会分叉。
    /// </para>
    /// </remarks>
    public IReadOnlyList<ResolvedTemplateSlot> ResolveDetailed(string id)
    {
        lock (_lock)
        {
            // order 记首次出现顺序，defs 记最终定义。分开两份是因为
            // 「排在哪」与「用谁的定义」是两件事：被覆盖的槽位保持原位置，
            // 只是内容换成覆盖者的——否则改一下类型，这一项就会跳到列表末尾。
            var order = new List<string>();
            var defs = new Dictionary<string, ResolvedTemplateSlot>(StringComparer.OrdinalIgnoreCase);

            Expand_NoLock(id, id, order, defs, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            return order.Select(n => defs[n]).ToList();
        }
    }

    /// <summary>递归展开。调用方须持有 <see cref="_lock"/>。</summary>
    /// <param name="order">按首次出现顺序记录功能名，决定最终排列。</param>
    /// <param name="defs">功能名 → 当前生效的定义；后写入者覆盖先写入者。</param>
    /// <param name="rootId">最外层模板 Id，用于判定某一项是否「自有」。</param>
    /// <param name="path">当前展开路径上的模板 Id，用于识别环。</param>
    private void Expand_NoLock(
        string id,
        string rootId,
        List<string> order,
        Dictionary<string, ResolvedTemplateSlot> defs,
        HashSet<string> path)
    {
        if (string.IsNullOrWhiteSpace(id)) return;

        // 已在当前路径上 → 成环，截断
        if (!path.Add(id))
        {
            _log.Warn("Templates", "模板引用成环，已截断：" + id);
            return;
        }

        WebDeviceTemplate? t = _items.FirstOrDefault(x => x.Id == id);
        if (t is null) { path.Remove(id); return; }

        // 先展开引用、后写自有：自有因此是最后写入的，也就覆盖掉同名的引用项
        foreach (string child in t.Includes)
            Expand_NoLock(child, rootId, order, defs, path);

        foreach (WebDeviceTemplateSlot s in t.Slots)
        {
            string name = s.Name.Trim();
            if (name.Length == 0) continue;

            if (!defs.ContainsKey(name)) order.Add(name);   // 位置只在首次出现时确定

            // 定义永远取最后写入的；来源记的是「谁给出了这个生效定义」，
            // 因此被覆盖时来源也跟着换——界面才不会把一项标成继承却其实可编辑
            bool own = string.Equals(id, rootId, StringComparison.OrdinalIgnoreCase);
            defs[name] = new ResolvedTemplateSlot(
                CloneSlot(s), own ? string.Empty : t.Id, own ? string.Empty : t.Name);
        }

        // 退出本层：兄弟分支各自引用同一个模板是合法的（菱形），不算环
        path.Remove(id);
    }

    /// <summary>
    /// 判断把 <paramref name="candidateInclude"/> 加进 <paramref name="templateId"/> 是否会成环。
    /// </summary>
    /// <remarks>
    /// 在界面上「添加引用」之前调用。让操作员当场看到"不能选这个"，
    /// 比事后在展开时静默截断要好得多——后者的表现是模板莫名少了几个槽位。
    /// </remarks>
    public bool WouldCreateCycle(string templateId, string candidateInclude)
    {
        if (string.IsNullOrWhiteSpace(templateId) || string.IsNullOrWhiteSpace(candidateInclude))
            return false;

        // 自引用是最直接的环
        if (string.Equals(templateId, candidateInclude, StringComparison.OrdinalIgnoreCase))
            return true;

        lock (_lock)
        {
            // 候选者（或它引用的任何一层）已经引用了本模板 → 加进来就成环
            return Reaches_NoLock(candidateInclude, templateId, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>from 沿引用链能否到达 target。调用方须持有 <see cref="_lock"/>。</summary>
    private bool Reaches_NoLock(string from, string target, HashSet<string> visited)
    {
        if (!visited.Add(from)) return false;
        if (string.Equals(from, target, StringComparison.OrdinalIgnoreCase)) return true;

        WebDeviceTemplate? t = _items.FirstOrDefault(x => x.Id == from);
        if (t is null) return false;

        return t.Includes.Any(child => Reaches_NoLock(child, target, visited));
    }

    public string ExportJson()
    {
        lock (_lock)
            return JsonSerializer.Serialize(new TemplatePack { Schema = "ck.device-template.v1", Templates = _items }, JsonOptions);
    }

    public int ImportJson(string json, bool replace)
    {
        TemplatePack? pack = JsonSerializer.Deserialize<TemplatePack>(json, JsonOptions);
        List<WebDeviceTemplate> incoming = pack?.Templates ?? new();
        if (incoming.Count == 0)
        {
            List<WebDeviceTemplate>? flat = JsonSerializer.Deserialize<List<WebDeviceTemplate>>(json, JsonOptions);
            incoming = flat ?? new();
        }

        lock (_lock)
        {
            if (replace)
                _items.Clear();

            foreach (WebDeviceTemplate t in incoming)
            {
                if (string.IsNullOrWhiteSpace(t.Id))
                    t.Id = Guid.NewGuid().ToString("N");
                int i = _items.FindIndex(x => x.Id == t.Id);
                if (i >= 0) _items[i] = Clone(t);
                else _items.Add(Clone(t));
            }
            Persist_NoLock();
        }
        Changed?.Invoke();
        return incoming.Count;
    }

    /// <summary>
    /// 从磁盘载入模板库。支持两种顶层结构：带 schema 的对象，或裸的模板数组。
    /// </summary>
    /// <remarks>
    /// 两种格式必须各自 try：<c>Deserialize&lt;TemplatePack&gt;</c> 遇到数组是<b>抛异常</b>
    /// 而不是返回 null。此前两次反序列化共用一个 try，扁平数组的回退分支
    /// 因此永远执行不到——手写或导出的数组式模板文件会静默载入失败，
    /// 界面上模板列表空空如也，只在日志里留一行看不懂的类型转换错误。
    /// </remarks>
    private void Load()
    {
        string json;
        try
        {
            if (!File.Exists(WebPaths.TemplatesFile)) return;
            json = File.ReadAllText(WebPaths.TemplatesFile);
        }
        catch (Exception ex)
        {
            _log.Error("WebTemplateStore", "读取模板库文件失败: " + ex.Message);
            return;
        }

        // 首选格式：{ "schema": ..., "templates": [...] }
        try
        {
            TemplatePack? pack = JsonSerializer.Deserialize<TemplatePack>(json, JsonOptions);
            if (pack?.Templates is { Count: > 0 })
            {
                _items.AddRange(pack.Templates.Select(Clone));
                return;
            }
        }
        catch (JsonException)
        {
            // 不是对象结构，落到下面按数组再试一次
        }

        // 兼容格式：顶层直接是模板数组
        try
        {
            List<WebDeviceTemplate>? flat =
                JsonSerializer.Deserialize<List<WebDeviceTemplate>>(json, JsonOptions);
            if (flat is { Count: > 0 })
            {
                _items.AddRange(flat.Select(Clone));
                return;
            }
        }
        catch (JsonException ex)
        {
            _log.Error("WebTemplateStore", "载入模板库失败，两种格式都解析不了: " + ex.Message);
            return;
        }

        // 两种格式都解析成功但内容为空：正常的"还没建过模板"，不记错误
    }

    /// <summary>
    /// 写回磁盘。调用方必须已持有 <see cref="_lock"/>。
    /// </summary>
    /// <remarks>
    /// 走 <see cref="JsonFileStore"/> 的原子写（临时文件 + 替换），与其它配置存储一致。
    /// <para>
    /// 此前是直接 <c>File.WriteAllText</c> 覆写：写到一半掉电或进程被强杀，
    /// 磁盘上会留下一个被截断的 JSON。而 <see cref="Load"/> 解析失败时会
    /// <b>静默回落成空模板库</b>——现场表现是「模板一个都没了」，
    /// 既没有报错也没有线索，而模板往往是花时间一条条建起来的。
    /// </para>
    /// </remarks>
    private void Persist_NoLock()
    {
        TemplatePack pack = new() { Schema = "ck.device-template.v1", Templates = _items };

        if (!JsonFileStore.SaveObject(WebPaths.TemplatesFile, pack, out string error))
            _log.Error("WebTemplateStore", "保存模板库失败: " + error);
    }

    /// <summary>深拷贝一个模板。</summary>
    /// <remarks>
    /// 新增字段务必同步加到这里：本方法是模板进出存储的唯一通道，
    /// 漏一个字段的表现是「界面上改了、点保存也没报错，一刷新又回到原样」。
    /// <c>Includes</c> 必须 <c>ToList()</c> 复制而不是直接赋引用——
    /// 共享同一个 List 会让"编辑中的副本"和"已保存的记录"一起变，
    /// 取消编辑也退不回去。
    /// </remarks>
    private static WebDeviceTemplate Clone(WebDeviceTemplate t) => new()
    {
        Id = t.Id,
        Name = t.Name,
        Includes = t.Includes.ToList(),
        Slots = t.Slots.Select(CloneSlot).ToList()
    };

    /// <summary>深拷贝一条功能槽。</summary>
    private static WebDeviceTemplateSlot CloneSlot(WebDeviceTemplateSlot s) => new()
    {
        Name = s.Name,
        DataType = s.DataType,
        Note = s.Note ?? string.Empty,
        Length = s.Length,
        // 新增字段务必加到这里：CloneSlot 是槽位进出存储的唯一通道，
        // 漏一个的表现是「改了能保存、一刷新又变回默认」，且不报错
        Access = s.Access
    };

    private sealed class TemplatePack
    {
        public string Schema { get; set; } = "ck.device-template.v1";
        public List<WebDeviceTemplate> Templates { get; set; } = new();
    }
}
