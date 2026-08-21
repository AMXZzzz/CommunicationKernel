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
    /// 本地 JSON 设备仓储（System.Text.Json / net8）。
    /// 文件格式 version=1：{ "version":1, "devices":[ ... ] }；兼容旧版根数组。
    /// </summary>
    public class JsonDeviceRepository : IDeviceRepository {
        public const int CurrentVersion = 1;

        private readonly string _filePath;
        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public JsonDeviceRepository (string filePath) {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("路径不能为空", nameof(filePath));
            _filePath = filePath;
        }

        public IList<DeviceInfo> LoadAll () {
            if (!File.Exists(_filePath))
                return new List<DeviceInfo>();

            string json;
            try {
                json = File.ReadAllText(_filePath);
            } catch (Exception ex) {
                Trace.TraceWarning("读取设备配置失败: {0}", ex.Message);
                return new List<DeviceInfo>();
            }

            if (string.IsNullOrWhiteSpace(json))
                return new List<DeviceInfo>();

            try {
                return ParseDocument(json);
            } catch (Exception ex) {
                Trace.TraceWarning("解析设备配置失败: {0}", ex.Message);
                return new List<DeviceInfo>();
            }
        }

        public void SaveAll (IList<DeviceInfo> devices) {
            if (devices == null)
                devices = new List<DeviceInfo>();

            string dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var list = new List<DeviceDto>();
            foreach (DeviceInfo d in devices) {
                if (d == null) continue;
                list.Add(ToDto(d));
            }

            var doc = new DeviceFileDocument {
                version = CurrentVersion,
                devices = list
            };

            string json = JsonSerializer.Serialize(doc, WriteOptions);
            string tempPath = _filePath + ".tmp";
            try {
                File.WriteAllText(tempPath, json);
                if (File.Exists(_filePath))
                    File.Delete(_filePath);
                File.Move(tempPath, _filePath);
            } catch (Exception ex) {
                Trace.TraceWarning("保存设备配置失败: {0}", ex.Message);
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        private static IList<DeviceInfo> ParseDocument (string json) {
            using (JsonDocument doc = JsonDocument.Parse(json)) {
                JsonElement root = doc.RootElement;
                var result = new List<DeviceInfo>();

                if (root.ValueKind == JsonValueKind.Array) {
                    foreach (JsonElement el in root.EnumerateArray()) {
                        DeviceInfo d = FromElement(el);
                        if (d != null) result.Add(d);
                    }
                    return result;
                }

                if (root.ValueKind == JsonValueKind.Object) {
                    if (root.TryGetProperty("devices", out JsonElement arr) &&
                        arr.ValueKind == JsonValueKind.Array) {
                        foreach (JsonElement el in arr.EnumerateArray()) {
                            DeviceInfo d = FromElement(el);
                            if (d != null) result.Add(d);
                        }
                    }
                }

                return result;
            }
        }

        private static DeviceInfo FromElement (JsonElement el) {
            if (el.ValueKind != JsonValueKind.Object)
                return null;

            var d = new DeviceInfo();
            d.Id = GetString(el, "Id") ?? Guid.NewGuid().ToString("N");
            d.Name = GetString(el, "Name") ?? "新设备";
            d.Model = GetString(el, "Model") ?? "";
            d.Protocol = GetString(el, "Protocol") ?? "";
            d.Ip = GetString(el, "Ip") ?? "";
            d.Port = GetInt(el, "Port", 502);
            d.StationNo = GetInt(el, "StationNo", GetInt(el, "UnitId", 1));
            d.ExtraSettingsJson = GetString(el, "ExtraSettingsJson");
            if (string.IsNullOrWhiteSpace(d.ExtraSettingsJson))
                d.ExtraSettingsJson = "{}";

            d.IsConnected = false;
            d.StatusType = DeviceStatusType.Offline;

            int lane;
            if (TryGetInt(el, "Lane", out lane))
                d.Lane = (LaneType)lane;
            int bo;
            if (TryGetInt(el, "ByteOrder", out bo))
                d.ByteOrder = (ByteOrder)bo;
            int wo;
            if (TryGetInt(el, "WordOrder", out wo))
                d.WordOrder = (WordOrder)wo;
            int se;
            if (TryGetInt(el, "StringEncoding", out se))
                d.StringEncoding = (StringEncodingKind)se;

            return d;
        }

        private static DeviceDto ToDto (DeviceInfo d) {
            return new DeviceDto {
                Id = d.Id,
                Name = d.Name,
                Model = d.Model,
                Protocol = d.Protocol,
                Ip = d.Ip,
                Port = d.Port,
                StationNo = d.StationNo,
                ExtraSettingsJson = d.ExtraSettingsJson ?? "{}",
                Lane = (int)d.Lane,
                ByteOrder = (int)d.ByteOrder,
                WordOrder = (int)d.WordOrder,
                StringEncoding = (int)d.StringEncoding
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

        public class DeviceFileDocument {
            public int version { get; set; }
            public List<DeviceDto> devices { get; set; }
        }

        public class DeviceDto {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Model { get; set; }
            public string Protocol { get; set; }
            public string Ip { get; set; }
            public int Port { get; set; }
            public int StationNo { get; set; }
            public string ExtraSettingsJson { get; set; }
            public int Lane { get; set; }
            public int ByteOrder { get; set; }
            public int WordOrder { get; set; }
            public int StringEncoding { get; set; }
        }
    }
}
