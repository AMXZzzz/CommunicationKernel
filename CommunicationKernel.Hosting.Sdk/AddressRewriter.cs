#nullable disable

// -----------------------------------------------------------------------------
// 文件: AddressRewriter.cs
// 层级: 客户端层 — Hosting.Sdk 变量维护辅助
// 作用: 批量改写变量地址，先算出「谁会变成什么」，由调用方确认后再落库。
//
// 为什么在这里而不在某个 UI 里:
//   换 PLC 型号是常见维护动作——同一台机器、同一批变量，只是地址体系全换了
//   （Modbus 的 40001 → S7 的 DB1.DBW0 → MEWTOCOL 的 DT100）。
//   两个上位机都需要这个能力，且这部分是纯计算、没有 I/O，
//   放在这里两端共用并且可被测试覆盖。与 ValueCodec / JsonFileStore 同理。
//
// 刻意不做的事:
//   不理解任何协议的地址语法。它只做文本替换——
//   「40001 是不是合法的保持寄存器」属于协议知识，只能存在于插件 DLL 内，
//   本类型一旦开始校验地址格式就越过了那条线。
//   地址对不对，由连接后的实际读取来验证。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CommunicationKernel.Hosting.Sdk
{
    /// <summary>
    /// 一条参与批量改写的变量。
    /// </summary>
    /// <param name="Id">变量唯一标识，改写结果按它回填。</param>
    /// <param name="Name">变量名，仅用于预览时让人认出是哪一条。</param>
    /// <param name="Address">当前地址原文。</param>
    public sealed record AddressCandidate(string Id, string Name, string Address);

    /// <summary>
    /// 一条<b>确实会发生变化</b>的地址改写。
    /// </summary>
    /// <remarks>
    /// 只有新旧不同的条目才会出现在结果里——把「替换后没变化」的也列出来，
    /// 会让预览表里混进一堆噪声，操作员无法一眼看出这次到底影响了几条。
    /// </remarks>
    /// <param name="Id">变量唯一标识。</param>
    /// <param name="Name">变量名。</param>
    /// <param name="OldAddress">改写前的地址。</param>
    /// <param name="NewAddress">改写后的地址。</param>
    public sealed record AddressRewrite(string Id, string Name, string OldAddress, string NewAddress);

    /// <summary>
    /// 变量地址的批量改写计算。纯函数，不触碰存储。
    /// </summary>
    public static class AddressRewriter
    {
        /// <summary>
        /// 用户正则的执行超时。
        /// </summary>
        /// <remarks>
        /// 正则由操作员现场输入，形如 <c>(a+)+$</c> 的写法会触发灾难性回溯，
        /// 在没有超时的情况下会把界面线程钉死，且看不出是正则的问题。
        /// 一秒对几百条变量的替换绰绰有余。
        /// </remarks>
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

        /// <summary>
        /// 计算批量改写的预览结果。
        /// </summary>
        /// <param name="candidates">参与改写的变量集合；null 视为空集。</param>
        /// <param name="find">查找内容。普通模式为字面量，正则模式为模式串。</param>
        /// <param name="replaceWith">替换为的内容；可为空字符串（表示删除匹配部分）。</param>
        /// <param name="useRegex">
        /// true 走正则，<paramref name="replaceWith"/> 中可用 <c>$1</c> 引用捕获组。
        /// </param>
        /// <param name="changes">输出实际会变化的条目；失败时为空列表，绝不为 null。</param>
        /// <param name="error">失败原因；成功时为 null。</param>
        /// <returns>计算是否成功。注意：成功但 <paramref name="changes"/> 为空表示「没有匹配项」。</returns>
        /// <remarks>
        /// 返回值与结果条数是<b>两件事</b>：正则写错是失败（要拦住用户），
        /// 而「一条也没匹配上」是成功但无变化（要如实告诉用户没匹配到）。
        /// 把两者混为一谈会让操作员分不清是自己写错了还是本来就没有。
        /// </remarks>
        public static bool TryPreview(
            IEnumerable<AddressCandidate> candidates,
            string find,
            string replaceWith,
            bool useRegex,
            out IReadOnlyList<AddressRewrite> changes,
            out string error)
        {
            List<AddressRewrite> result = new();
            changes = result;
            error = null;

            // 查找内容为空时，普通替换会在每个字符间插入，正则会匹配所有空位置——
            // 两种都是把地址搅烂，直接拦住
            if (string.IsNullOrEmpty(find))
            {
                error = "查找内容不能为空";
                return false;
            }

            replaceWith ??= string.Empty;

            Regex regex = null;
            if (useRegex)
            {
                try
                {
                    regex = new Regex(find, RegexOptions.None, RegexTimeout);
                }
                catch (ArgumentException ex)
                {
                    // 正则语法错误。原样带上 .NET 的说明，那比"格式不正确"有用得多
                    error = "正则表达式无效：" + ex.Message;
                    return false;
                }
            }

            if (candidates is null)
                return true;

            foreach (AddressCandidate c in candidates)
            {
                if (c is null) continue;

                string oldAddress = c.Address ?? string.Empty;
                string newAddress;

                if (useRegex)
                {
                    try
                    {
                        newAddress = regex.Replace(oldAddress, replaceWith);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        // 灾难性回溯。整批中止而非跳过这一条：
                        // 部分成功会留下一半改过一半没改的变量表，比不改更难收拾
                        error = "正则匹配超时，通常是模式写法导致灾难性回溯（例如嵌套的 (a+)+）。";
                        changes = new List<AddressRewrite>();
                        return false;
                    }
                    catch (ArgumentException ex)
                    {
                        // 替换串里引用了不存在的捕获组，例如写了 $2 但只有一个分组
                        error = "替换内容无效：" + ex.Message;
                        changes = new List<AddressRewrite>();
                        return false;
                    }
                }
                else
                {
                    // 普通模式用 Ordinal：地址是协议文本，不能受运行机器的区域设置影响
                    newAddress = oldAddress.Replace(find, replaceWith, StringComparison.Ordinal);
                }

                // 只收真正发生变化的
                if (!string.Equals(newAddress, oldAddress, StringComparison.Ordinal))
                    result.Add(new AddressRewrite(c.Id, c.Name, oldAddress, newAddress));
            }

            return true;
        }
    }
}
