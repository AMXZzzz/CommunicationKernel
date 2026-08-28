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
}

/// <summary>一类设备的功能模板。</summary>
public sealed class WebDeviceTemplate
{
    /// <summary>稳定 Id，变量行用它标记来源。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>显示名，例如「变频器」。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>必须具备的功能槽。</summary>
    public List<WebDeviceTemplateSlot> Slots { get; set; } = new();
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
            Persist_NoLock();
        }
        Changed?.Invoke();
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

    private void Persist_NoLock()
    {
        try
        {
            File.WriteAllText(WebPaths.TemplatesFile,
                JsonSerializer.Serialize(new TemplatePack { Schema = "ck.device-template.v1", Templates = _items }, JsonOptions));
        }
        catch (Exception ex)
        {
            _log.Error("WebTemplateStore", "保存模板库失败: " + ex.Message);
        }
    }

    private static WebDeviceTemplate Clone(WebDeviceTemplate t) => new()
    {
        Id = t.Id,
        Name = t.Name,
        Slots = t.Slots.Select(s => new WebDeviceTemplateSlot
        {
            Name = s.Name,
            DataType = s.DataType,
            Note = s.Note ?? string.Empty,
            Length = s.Length
        }).ToList()
    };

    private sealed class TemplatePack
    {
        public string Schema { get; set; } = "ck.device-template.v1";
        public List<WebDeviceTemplate> Templates { get; set; } = new();
    }
}
