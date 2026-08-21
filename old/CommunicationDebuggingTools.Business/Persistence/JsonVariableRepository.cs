using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunicationDebuggingTools.Core.Enums;
using CommunicationDebuggingTools.Core.Interfaces;
using CommunicationDebuggingTools.Core.Models;

namespace CommunicationDebuggingTools.Business.Persistence {
    /// <summary>
    /// 本地 JSON 变量仓储（System.Text.Json / net8）。
    /// 格式 version=1：{ "version":1, "variables":[ ... ] }；兼容旧版根数组。
    /// </summary>
    public class JsonVariableRepository : IVariableRepository {
        public const int CurrentVersion = 1;

        private readonly string _filePath;
        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public JsonVariableRepository (string filePath) {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("路径不能为空", nameof(filePath));
            _filePath = filePath;
        }

        public IList<VariableItem> LoadAll () {
            if (!File.Exists(_filePath))
                return new List<VariableItem>();

            string json;
            try {
                json = File.ReadAllText(_filePath);
            } catch (Exception ex) {
                Trace.TraceWarning("读取变量配置失败: {0}", ex.Message);
                return new List<VariableItem>();
            }

            if (string.IsNullOrWhiteSpace(json))
                return new List<VariableItem>();

            try {
                return ParseDocument(json);
            } catch (Exception ex) {
                Trace.TraceWarning("解析变量配置失败: {0}", ex.Message);
                return new List<VariableItem>();
            }
        }

        public void SaveAll (IList<VariableItem> items) {
            if (items == null)
                items = new List<VariableItem>();

            string dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var list = new List<VariableDto>();
            foreach (VariableItem v in items) {
                if (v == null) continue;
                list.Add(ToDto(v));
            }

            var doc = new VariableFileDocument {
                version = CurrentVersion,
                variables = list
            };

            string json = JsonSerializer.Serialize(doc, WriteOptions);
            string tempPath = _filePath + ".tmp";
            try {
                File.WriteAllText(tempPath, json);
                if (File.Exists(_filePath))
                    File.Delete(_filePath);
                File.Move(tempPath, _filePath);
            } catch (Exception ex) {
                Trace.TraceWarning("保存变量配置失败: {0}", ex.Message);
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        private static IList<VariableItem> ParseDocument (string json) {
            using (JsonDocument doc = JsonDocument.Parse(json)) {
                JsonElement root = doc.RootElement;
                var result = new List<VariableItem>();

                if (root.ValueKind == JsonValueKind.Array) {
                    foreach (JsonElement el in root.EnumerateArray()) {
                        VariableItem v = FromElement(el);
                        if (v != null) result.Add(v);
                    }
                    return result;
                }

                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("variables", out JsonElement arr) &&
                    arr.ValueKind == JsonValueKind.Array) {
                    foreach (JsonElement el in arr.EnumerateArray()) {
                        VariableItem v = FromElement(el);
                        if (v != null) result.Add(v);
                    }
                }

                return result;
            }
        }

        private static VariableItem FromElement (JsonElement el) {
            if (el.ValueKind != JsonValueKind.Object)
                return null;

            var v = new VariableItem();
            v.Id = GetString(el, "Id") ?? Guid.NewGuid().ToString("N");
            v.DeviceId = GetString(el, "DeviceId") ?? "";
            v.Name = GetString(el, "Name") ?? "新变量";
            v.Address = GetString(el, "Address") ?? "";
            v.Unit = GetString(el, "Unit") ?? "";
            v.Category = GetString(el, "Category") ?? "状态点";
            v.Description = GetString(el, "Description") ?? "";
            v.Length = GetInt(el, "Length", 0);

            int dt;
            if (TryGetInt(el, "DataType", out dt))
                v.DataType = (VariableDataType)dt;
            int ac;
            if (TryGetInt(el, "Access", out ac))
                v.Access = (VariableAccess)ac;

            int scan;
            if (TryGetInt(el, "ScanRateMs", out scan) && scan > 0)
                v.ScanRateMs = scan;
            bool poll;
            if (TryGetBool(el, "IsPollingEnabled", out poll))
                v.IsPollingEnabled = poll;

            return v;
        }

        private static VariableDto ToDto (VariableItem v) {
            return new VariableDto {
                Id = v.Id,
                DeviceId = v.DeviceId,
                Name = v.Name,
                Address = v.Address,
                DataType = (int)v.DataType,
                Access = (int)v.Access,
                Length = v.Length,
                Unit = v.Unit ?? "",
                Category = v.Category ?? "",
                Description = v.Description ?? "",
                ScanRateMs = v.ScanRateMs,
                IsPollingEnabled = v.IsPollingEnabled
            };
        }

        private static string GetString (JsonElement el, string name) {
            if (!el.TryGetProperty(name, out JsonElement p))
                return null;
            if (p.ValueKind == JsonValueKind.String)
                return p.GetString();
            if (p.ValueKind == JsonValueKind.Null || p.ValueKind == JsonValueKind.Undefined)
                return null;
            return p.ToString();
        }

        private static int GetInt (JsonElement el, string name, int def) {
            int v;
            return TryGetInt(el, name, out v) ? v : def;
        }

        private static bool TryGetInt (JsonElement el, string name, out int value) {
            value = 0;
            if (!el.TryGetProperty(name, out JsonElement p))
                return false;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out value))
                return true;
            if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out value))
                return true;
            return false;
        }

        private static bool TryGetBool (JsonElement el, string name, out bool value) {
            value = false;
            if (!el.TryGetProperty(name, out JsonElement p))
                return false;
            if (p.ValueKind == JsonValueKind.True) { value = true; return true; }
            if (p.ValueKind == JsonValueKind.False) { value = false; return true; }
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int n)) {
                value = n != 0;
                return true;
            }
            if (p.ValueKind == JsonValueKind.String && bool.TryParse(p.GetString(), out value))
                return true;
            return false;
        }

        public class VariableFileDocument {
            public int version { get; set; }
            public List<VariableDto> variables { get; set; }
        }

        public class VariableDto {
            public string Id { get; set; }
            public string DeviceId { get; set; }
            public string Name { get; set; }
            public string Address { get; set; }
            public int DataType { get; set; }
            public int Access { get; set; }
            public int Length { get; set; }
            public string Unit { get; set; }
            public string Category { get; set; }
            public string Description { get; set; }
            public int ScanRateMs { get; set; }
            public bool IsPollingEnabled { get; set; }
        }
    }
}
