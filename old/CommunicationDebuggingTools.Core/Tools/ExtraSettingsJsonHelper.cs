using System;
using System.Text.Json;

namespace CommunicationDebuggingTools.Core.Tools {

    /// <summary>
    /// 解析 <c>ExtraSettingsJson</c> 中的扁平整型字段（仅插件内部使用）。
    /// Core / Business / UI 不得用本类解读业务语义；站号禁止出现在本 JSON 中。
    /// </summary>
    public static class ExtraSettingsJsonHelper {

        /// <summary>读取整型字段；缺失或非法时返回 defaultValue。</summary>
        public static int GetInt (string json, string key, int defaultValue) {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(key))
                return defaultValue;

            try {
                using (JsonDocument doc = JsonDocument.Parse(json)) {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        return defaultValue;

                    foreach (JsonProperty prop in doc.RootElement.EnumerateObject()) {
                        if (!string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase))
                            continue;

                        JsonElement v = prop.Value;
                        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n))
                            return n;
                        if (v.ValueKind == JsonValueKind.String &&
                            int.TryParse(v.GetString(), out int p))
                            return p;
                        return defaultValue;
                    }
                }
            } catch {
            }

            return defaultValue;
        }
    }
}
