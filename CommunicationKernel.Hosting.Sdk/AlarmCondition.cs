// -----------------------------------------------------------------------------
// 文件: AlarmCondition.cs
// 层级: Hosting.Sdk — 报警条件的判定
// 作用: 把「某个点位满足什么条件时算报警」这条规则，对着一个读值算出真假。
//
// 为什么在 Sdk 而不是界面工程里：
//   这是整个 MES 里最容易出错、也最难在界面上发现错误的一段。判反了不会有
//   任何提示——设备好好的却一直报警，或者真出事了一声不吭，两种都要等到
//   现场出问题才暴露。这类东西必须能写测试，而测试工程只引用 Sdk。
//
//   它和 ValueCodec、VariableScale 是同一类：纯取值判断，不碰界面也不碰通讯。
//
// 判定用的是<b>显示值</b>，即已经过字节序解码与小数位换算之后的那个字符串。
// 这样「气压 < 0.45」里写的 0.45 就是操作员在变量表里看到的那个数，
// 不必再去想寄存器里存的是 45 还是 450。
// 代价是位判断要求该点位没有配小数位——而报警字、状态字本来就不会配。
// -----------------------------------------------------------------------------

using System.Globalization;

namespace CommunicationKernel.Hosting.Sdk;

/// <summary>报警条件的比较方式。</summary>
/// <remarks>
/// 分三族，对应三类点位：
/// <list type="bullet">
///   <item><b>通断</b>——线圈 / Bool。它本身就是一个位，没有「第几位」可言。</item>
///   <item><b>位</b>——报警字、状态字这类把多个标志打包进一个寄存器的整数。</item>
///   <item><b>值</b>——气压、温度、计数这类整个读数有意义的量。</item>
/// </list>
/// 三族不通用：给线圈选「第 N 位 = 1」没有意义，给浮点选位比较更是错的。
/// 界面上能选什么由所选变量的数据类型决定（见 <see cref="AlarmOpInfo.For"/>）。
/// </remarks>
public enum AlarmOp
{
    /// <summary>该位为 1。报警字的常规用法。</summary>
    BitSet = 0,

    /// <summary>该位为 0。用于「就绪信号消失」这类反逻辑。</summary>
    BitClear,

    /// <summary>整个值等于。</summary>
    Equal,

    /// <summary>整个值不等于。</summary>
    NotEqual,

    /// <summary>整个值小于。</summary>
    Less,

    /// <summary>整个值大于。</summary>
    Greater,

    /// <summary>线圈接通（ON）。</summary>
    /// <remarks>只用于 Bool。它不带操作数——线圈就是一位，没有「和谁比」。</remarks>
    IsOn,

    /// <summary>线圈断开（OFF）。</summary>
    IsOff,
}

/// <summary>比较方式的适用范围、显示名与判定。</summary>
public static class AlarmOpInfo
{
    /// <summary>全部取值，仅供遍历与迁移使用。</summary>
    /// <remarks>
    /// 界面上<b>不要</b>直接用这个：能选什么取决于变量类型，
    /// 全列出来会让人给线圈选「值 &lt;」这种永远不成立的条件。用 <see cref="For"/>。
    /// </remarks>
    public static readonly AlarmOp[] All =
    {
        AlarmOp.IsOn, AlarmOp.IsOff,
        AlarmOp.BitSet, AlarmOp.BitClear,
        AlarmOp.Equal, AlarmOp.NotEqual, AlarmOp.Less, AlarmOp.Greater,
    };

    /// <summary>线圈可用的比较方式。</summary>
    private static readonly AlarmOp[] BoolOps = { AlarmOp.IsOn, AlarmOp.IsOff };

    /// <summary>整数可用的比较方式：位与值都行。</summary>
    private static readonly AlarmOp[] IntegerOps =
    {
        AlarmOp.BitSet, AlarmOp.BitClear,
        AlarmOp.Equal, AlarmOp.NotEqual, AlarmOp.Less, AlarmOp.Greater,
    };

    /// <summary>浮点可用的比较方式：只有值。</summary>
    /// <remarks>
    /// 浮点没有「第几位」——它的二进制里那些位是尾数和指数，
    /// 按位取出来是一串与工程量毫无关系的数。
    /// </remarks>
    private static readonly AlarmOp[] FloatOps =
    {
        AlarmOp.Equal, AlarmOp.NotEqual, AlarmOp.Less, AlarmOp.Greater,
    };

    /// <summary>文本 / 原始字节可用的比较方式：只有相等与否。</summary>
    /// <remarks>大小比较对字符串没有现场意义，留着只会让人误以为能按字典序报警。</remarks>
    private static readonly AlarmOp[] TextOps = { AlarmOp.Equal, AlarmOp.NotEqual };

    /// <summary>某个数据类型能用哪些比较方式。</summary>
    /// <param name="dataType">变量的数据类型名；空串表示还没选变量。</param>
    /// <remarks>还没选变量时给整数那一套——它最常见，选了变量之后界面会纠正。</remarks>
    public static AlarmOp[] For(string dataType) => dataType switch
    {
        "Bool" => BoolOps,
        "Float" or "Double" => FloatOps,
        "String" or "Hex" => TextOps,
        _ => IntegerOps,
    };

    /// <summary>该比较方式对这个数据类型是否适用。</summary>
    public static bool Applies(AlarmOp op, string dataType) =>
        Array.IndexOf(For(dataType), op) >= 0;

    /// <summary>下拉框里的中文名。</summary>
    /// <remarks>
    /// 位比较写「第 N 位」，值比较写符号，线圈写通断：三者的操作数分别是
    /// 位号、数值、没有。名字里带上这个差别，右边那个框该填什么就不用再猜。
    /// </remarks>
    public static string Label(AlarmOp op) => op switch
    {
        AlarmOp.IsOn => "接通 ON",
        AlarmOp.IsOff => "断开 OFF",
        AlarmOp.BitSet => "第 N 位 = 1",
        AlarmOp.BitClear => "第 N 位 = 0",
        AlarmOp.Equal => "值 ==",
        AlarmOp.NotEqual => "值 !=",
        AlarmOp.Less => "值 <",
        AlarmOp.Greater => "值 >",
        _ => "第 N 位 = 1",
    };

    /// <summary>右侧输入框的占位提示。</summary>
    public static string Placeholder(AlarmOp op) => IsBit(op) ? "位号 0-15" : "对比值";

    /// <summary>是不是位比较。位比较的操作数是位号，不是数值。</summary>
    public static bool IsBit(AlarmOp op) => op is AlarmOp.BitSet or AlarmOp.BitClear;

    /// <summary>这个比较方式要不要操作数。</summary>
    /// <remarks>
    /// 线圈的通断不需要：条件已经由方式本身表达完了。
    /// 还留一个输入框在那儿，会让人以为漏填了什么。
    /// </remarks>
    public static bool NeedsOperand(AlarmOp op) => op is not (AlarmOp.IsOn or AlarmOp.IsOff);

    /// <summary>拼成一句可读的条件，用于日志与只读展示。</summary>
    public static string Text(AlarmOp op, string operand)
    {
        string v = string.IsNullOrWhiteSpace(operand) ? "?" : operand.Trim();

        return op switch
        {
            AlarmOp.IsOn => "= ON",
            AlarmOp.IsOff => "= OFF",
            AlarmOp.BitSet => "bit " + v + " = 1",
            AlarmOp.BitClear => "bit " + v + " = 0",
            AlarmOp.Equal => "== " + v,
            AlarmOp.NotEqual => "!= " + v,
            AlarmOp.Less => "< " + v,
            AlarmOp.Greater => "> " + v,
            _ => v,
        };
    }

    /// <summary>
    /// 对着一个读值判定条件是否成立。
    /// </summary>
    /// <param name="op">比较方式。</param>
    /// <param name="operand">操作数：位比较时是位号，值比较时是对比值，通断不用。</param>
    /// <param name="value">
    /// 该点位的<b>显示值</b>——已经过字节序解码与小数位换算的字符串。
    /// </param>
    /// <returns>
    /// 成立为 <c>true</c>，不成立为 <c>false</c>；
    /// <b>判不了</b>时为 <c>null</c>（值读不到、格式不符、操作数填错）。
    /// </returns>
    /// <remarks>
    /// 三态而不是布尔。判不了绝不能当成 <c>false</c>：
    /// 那会让一条因为读不到值而失效的规则，在界面上和「一切正常」长得一模一样。
    /// 调用方拿到 <c>null</c> 应当把这条规则显示成「无法判定」，而不是悄悄跳过。
    /// </remarks>
    public static bool? Evaluate(AlarmOp op, string operand, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        string v = value.Trim();

        // 读失败时这一列填的是错误码（TIMEOUT 之类），"--" 是尚未读取。
        // 两者都不是数据，不能拿去比
        if (v == "--") return null;

        switch (op)
        {
            case AlarmOp.IsOn:
            case AlarmOp.IsOff:
            {
                bool? on = AsBool(v);
                if (on is null) return null;
                return op == AlarmOp.IsOn ? on : !on;
            }

            case AlarmOp.BitSet:
            case AlarmOp.BitClear:
            {
                if (!int.TryParse(operand?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int bit))
                    return null;
                if (bit < 0 || bit > 63) return null;

                // 位判断必须落在整数上。该点位配了小数位的话，显示值是 "50.4"，
                // 取它的第几位毫无意义——这种情况判不了，交回 null
                if (!long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long raw))
                    return null;

                bool set = ((raw >> bit) & 1L) == 1L;
                return op == AlarmOp.BitSet ? set : !set;
            }

            case AlarmOp.Equal:
            case AlarmOp.NotEqual:
            {
                bool same = SameValue(v, operand);
                return op == AlarmOp.Equal ? same : !same;
            }

            case AlarmOp.Less:
            case AlarmOp.Greater:
            {
                if (!decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal left))
                    return null;
                if (!decimal.TryParse(operand?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal right))
                    return null;

                return op == AlarmOp.Less ? left < right : left > right;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// 把显示值理解成通断。
    /// </summary>
    /// <remarks>
    /// ValueCodec 把 Bool 解码成 "ON" / "OFF"，但手改过的配置或别的类型也可能
    /// 给出 1/0、true/false。都认，认不出的返回 null 而不是当成 OFF——
    /// 把一个读不懂的值判成「断开」，正好会让一条 IsOff 的报警凭空成立。
    /// </remarks>
    private static bool? AsBool(string v)
    {
        if (v.Equals("ON", StringComparison.OrdinalIgnoreCase)) return true;
        if (v.Equals("OFF", StringComparison.OrdinalIgnoreCase)) return false;
        if (bool.TryParse(v, out bool b)) return b;

        if (decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d))
            return d != 0m;

        return null;
    }

    /// <summary>
    /// 判两个值相不相等。
    /// </summary>
    /// <remarks>
    /// 两边都能当数就按数比，否则按文本比（忽略大小写与首尾空白）。
    /// 只按文本比的话，"50.0" 和 "50" 会被判成不等——而它们是同一个读数，
    /// 差别只来自小数位怎么配。
    /// </remarks>
    private static bool SameValue(string value, string? operand)
    {
        string o = (operand ?? string.Empty).Trim();

        if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal a) &&
            decimal.TryParse(o, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal b))
        {
            return a == b;
        }

        return string.Equals(value, o, StringComparison.OrdinalIgnoreCase);
    }
}
