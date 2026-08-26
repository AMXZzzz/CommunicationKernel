#nullable disable

// -----------------------------------------------------------------------------
// 文件: JsonFileStore.cs
// 层级: 客户端层 — EngineHost.Sdk 本地持久化辅助
// 作用: 把一份列表原子地读写到 JSON 文件，供各 UI 的本地配置库共用。
//
// 为什么收敛到这里:
//   WPF 与 Web 各有一套设备/变量配置库，两边都在做"序列化成 JSON 写文件"，
//   但只有 WPF 写得是原子的（临时文件 + File.Replace），
//   Web 直接 File.WriteAllText 覆写——写到一半掉电或进程被杀，
//   留下的是一个被截断的 JSON，下次启动整份设备配置全部丢失，且没有任何提示。
//
//   这正是重复实现最典型的代价：两处逻辑看着一样，实际上一处有防护一处没有，
//   而缺的那处只在事故当天才会暴露。
//
// 刻意不做的事:
//   这里<b>只</b>抽取"原子读写 JSON 列表"这一层，不试图统一两个 UI 的配置库本身。
//   两边的功能并不对等（WPF 有导入导出与批量编辑，Web 没有），
//   强行合并只会把 WPF 的富功能塞进 Web 用不上的抽象里。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace CommunicationKernel.EngineHost.Sdk
{
    /// <summary>
    /// 原子地把一份列表读写到 JSON 文件。所有方法都不抛异常。
    /// </summary>
    /// <remarks>
    /// 本类型不持有任何状态，也不做并发控制——调用方的配置库自己有锁，
    /// 在这里再加一层只会制造嵌套锁的机会。
    /// </remarks>
    public static class JsonFileStore
    {
        /// <summary>
        /// 统一的序列化选项。
        /// </summary>
        /// <remarks>
        /// <c>UnsafeRelaxedJsonEscaping</c> 是必须的：默认编码器会把中文设备名
        /// 转义成 \uXXXX，文件用记事本打开是一片乱码，现场排查时没法直接看配置。
        /// </remarks>
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        };

        /// <summary>
        /// 从磁盘读取一份列表。
        /// </summary>
        /// <typeparam name="T">列表元素类型。</typeparam>
        /// <param name="path">文件绝对路径。</param>
        /// <param name="error">
        /// 读取失败时的描述，成功或文件不存在时为 null。
        /// 调用方通常把它写进应用日志。
        /// </param>
        /// <returns>
        /// 读到的列表；文件不存在、内容为 null、或解析失败时返回空列表。
        /// </returns>
        /// <remarks>
        /// 配置损坏不应阻止程序启动，因此这里以空列表继续，用户重新录入即可覆盖。
        /// <b>刻意不删除损坏文件</b>——留着便于事后排查。
        /// </remarks>
        public static List<T> Load<T>(string path, out string error)
        {
            error = null;

            try
            {
                // 首次启动尚无文件，返回空列表而非报错
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return new List<T>();

                string json = File.ReadAllText(path);
                List<T> loaded = JsonSerializer.Deserialize<List<T>>(json, SerializerOptions);

                // 文件内容是字面量 null 时反序列化结果也是 null
                return loaded ?? new List<T>();
            }
            catch (Exception ex)
            {
                // 包括 IO 异常与 JSON 语法错误，两者对调用方是同一种处置
                error = "读取 " + path + " 失败: " + ex.Message;
                return new List<T>();
            }
        }

        /// <summary>
        /// 原子地把一份列表写入磁盘。
        /// </summary>
        /// <typeparam name="T">列表元素类型。</typeparam>
        /// <param name="path">文件绝对路径；所在目录会被自动创建。</param>
        /// <param name="items">要写入的内容。</param>
        /// <param name="error">写入失败时的描述，成功时为 null。</param>
        /// <returns>写入是否成功。</returns>
        /// <remarks>
        /// <b>先写临时文件再替换。</b>直接覆盖时若在写入中途断电或崩溃，
        /// 会留下一个被截断的 JSON，下次启动直接丢失全部配置。
        /// </remarks>
        public static bool Save<T>(string path, IEnumerable<T> items, out string error)
        {
            error = null;

            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    error = "保存路径为空";
                    return false;
                }

                // 确保目标目录存在（首次运行时通常还没有）
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                string json = JsonSerializer.Serialize(
                    new List<T>(items ?? Array.Empty<T>()), SerializerOptions);

                // 写到同目录下的临时文件——必须同目录，跨卷时 File.Replace 会失败
                string tempPath = path + ".tmp";
                File.WriteAllText(tempPath, json);

                // 目标已存在则原子替换，否则直接改名
                if (File.Exists(path))
                    File.Replace(tempPath, path, null);
                else
                    File.Move(tempPath, path);

                return true;
            }
            catch (Exception ex)
            {
                // 落盘失败只影响下次启动的恢复能力，不应中断当前操作
                error = "保存 " + path + " 失败: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 原子地把单个对象写入 JSON 文件。
        /// </summary>
        /// <typeparam name="T">对象类型；可以是匿名类型。</typeparam>
        /// <param name="path">文件绝对路径；所在目录会被自动创建。</param>
        /// <param name="value">要写入的对象。</param>
        /// <param name="error">写入失败时的描述，成功时为 null。</param>
        /// <returns>写入是否成功。</returns>
        /// <remarks>
        /// 与 <see cref="Save{T}(string, IEnumerable{T}, out string)"/> 同样先写临时文件再替换。
        /// 设置类文件虽小，截断后果同样是"下次启动读不出来"。
        /// </remarks>
        public static bool SaveObject<T>(string path, T value, out string error)
        {
            error = null;

            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    error = "保存路径为空";
                    return false;
                }

                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                string json = JsonSerializer.Serialize(value, SerializerOptions);

                // 同目录临时文件：跨卷时 File.Replace 会失败
                string tempPath = path + ".tmp";
                File.WriteAllText(tempPath, json);

                if (File.Exists(path))
                    File.Replace(tempPath, path, null);
                else
                    File.Move(tempPath, path);

                return true;
            }
            catch (Exception ex)
            {
                error = "保存 " + path + " 失败: " + ex.Message;
                return false;
            }
        }
    }
}
