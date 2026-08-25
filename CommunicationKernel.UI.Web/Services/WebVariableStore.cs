// -----------------------------------------------------------------------------
// 文件: Services/WebVariableStore.cs
// 层级: UI 层 — Blazor Server
// 作用: 持久化变量定义。变量是上位机配置，不属于 Host.App。
// -----------------------------------------------------------------------------

using CommunicationKernel.Host.Sdk;
using System.Text.Json;

namespace CommunicationKernel.UI.Web.Services;

/// <summary>一条本地变量定义。当前值不落盘。</summary>
public sealed class WebVariable
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string RouteId { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    /// <summary>Bool / Int16 / UInt16 / Int32 / UInt32 / Float / Hex。</summary>
    public string DataType { get; set; } = "Int16";
    public int Length { get; set; } = 2;
    public bool Polling { get; set; }
    public int ScanRateMs { get; set; } = 1000;
    public string Unit { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayValue { get; set; } = "--";
    [System.Text.Json.Serialization.JsonIgnore]
    public string WriteText { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsError { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string LastUpdated { get; set; } = string.Empty;
}

/// <summary>变量表磁盘镜像。线程安全。</summary>
public sealed class WebVariableStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _lock = new();
    private readonly List<WebVariable> _items = new();
    private readonly AppLogStore _log;

    public event Action? Changed;

    public WebVariableStore(AppLogStore log)
    {
        _log = log;
        Load();
    }

    public IReadOnlyList<WebVariable> GetAll()
    {
        lock (_lock)
            return _items.ToList();
    }

    public void Add(WebVariable item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(item.Id))
                item.Id = Guid.NewGuid().ToString("N");
            _items.Add(item);
            Persist_NoLock();
        }
        Changed?.Invoke();
    }

    public void Update(WebVariable item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_lock)
        {
            int i = _items.FindIndex(v => v.Id == item.Id);
            if (i < 0) return;
            _items[i] = item;
            Persist_NoLock();
        }
        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            _items.RemoveAll(v => v.Id == id);
            Persist_NoLock();
        }
        Changed?.Invoke();
    }

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
    public void ApplyRead(string id, string display, bool error)
    {
        lock (_lock)
        {
            WebVariable? v = _items.FirstOrDefault(x => x.Id == id);
            if (v is null) return;
            v.DisplayValue = display;
            v.IsError = error;
            v.LastUpdated = DateTime.Now.ToString("HH:mm:ss");
        }
        Changed?.Invoke();
    }

    public string ExportJson()
    {
        lock (_lock)
            return JsonSerializer.Serialize(_items, JsonOptions);
    }

    public int ImportJson(string json, bool replace)
    {
        List<WebVariable>? list = JsonSerializer.Deserialize<List<WebVariable>>(json, JsonOptions);
        if (list is null) return 0;
        foreach (WebVariable v in list)
        {
            if (string.IsNullOrWhiteSpace(v.Id))
                v.Id = Guid.NewGuid().ToString("N");
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
