// -----------------------------------------------------------------------------
// 文件: PublicApiSurfaceTests.cs
// 层级: 测试
// 作用: 锁定两个打包库的公共 API 面，使其变更成为显式动作。
//
// 这两个库（Engine.Runtime 与 EngineHost.Sdk）会以 NuGet 包发给外部消费者，
// 公共成员一旦发布就是承诺：改签名、删成员、动可见性都会让下游编译不过。
//
// 为什么不用 Microsoft.CodeAnalysis.PublicApiAnalyzers：
//   它的基线文件要求精确的签名格式，而其代码修复只能在 IDE 里应用——
//   dotnet format 不修改 AdditionalFiles，CI 与命令行下无法生成基线。
//   手写一百多行签名极易出错，而错误的基线比没有基线更糟：
//   它会一边报着假警一边放过真正的破坏性改动。
//   这里的快照格式由本文件自己定义，因而永远能被自身重新生成。
//
// 基线更新方式：
//   公共 API 的变更是有意为之时，运行一次带 UPDATE_API_BASELINE=1 的测试，
//   快照文件会被就地重写，然后把 diff 一并提交、在评审时确认。
//     Windows: $env:UPDATE_API_BASELINE=1; dotnet test --filter PublicApiSurface
//     Linux:   UPDATE_API_BASELINE=1 dotnet test --filter PublicApiSurface
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace CommunicationKernel.Tests;

// 公共 API 快照：破坏性变更必须在评审里被看见，而不是上线后才被下游发现
[TestClass]
public class PublicApiSurfaceTests {

    // Engine.Runtime 的公开面必须与基线一致
    [TestMethod]
    public void EngineAssembly_PublicApi_MatchesBaseline()
        => AssertSurfaceMatchesBaseline(typeof(CommunicationKernel.Engine.Runtime.EngineRuntime).Assembly);

    // EngineHost.Sdk 的公开面必须与基线一致
    [TestMethod]
    public void ClientAssembly_PublicApi_MatchesBaseline()
        => AssertSurfaceMatchesBaseline(typeof(CommunicationKernel.EngineHost.Sdk.RouteReconcileGate).Assembly);

    // =========================================================================
    // 比对与基线维护
    // =========================================================================

    private static void AssertSurfaceMatchesBaseline(Assembly assembly) {
        string actual       = RenderPublicSurface(assembly);
        string baselinePath = ResolveBaselinePath(assembly.GetName().Name!);

        // 显式更新模式：重写基线并直接通过，供有意变更 API 时使用
        if (Environment.GetEnvironmentVariable("UPDATE_API_BASELINE") == "1") {
            Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
            File.WriteAllText(baselinePath, actual);
            return;
        }

        if (!File.Exists(baselinePath)) {
            Assert.Fail(
                $"缺少公共 API 基线 {baselinePath}。" +
                "首次建立请以 UPDATE_API_BASELINE=1 运行本测试，并提交生成的文件。");
        }

        // 统一换行：基线文件在 Windows 与 Linux 之间往返时会被 git 改写行尾，
        // 不归一化的话 CI 上会出现纯行尾差异导致的假失败
        string expected = Normalize(File.ReadAllText(baselinePath));

        if (Normalize(actual) == expected) return;

        Assert.Fail(
            $"{assembly.GetName().Name} 的公共 API 与基线不一致。\n\n" +
            "若这是有意的 API 变更，请以 UPDATE_API_BASELINE=1 重新运行本测试更新基线，\n" +
            "并把基线 diff 一并提交——公共 API 的每一次改动都应在评审中被看见。\n\n" +
            DescribeDifference(expected, Normalize(actual)));
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd();

    /// <summary>给出前若干行差异，避免整份 API 面刷屏。</summary>
    private static string DescribeDifference(string expected, string actual) {
        string[] expectedLines = expected.Split('\n');
        string[] actualLines   = actual.Split('\n');

        var removed = expectedLines.Except(actualLines).Take(15).ToList();
        var added   = actualLines.Except(expectedLines).Take(15).ToList();

        var sb = new StringBuilder();
        if (removed.Count > 0) {
            sb.AppendLine("已消失的公共成员（对下游是破坏性变更）：");
            foreach (string line in removed) sb.AppendLine("  - " + line);
        }
        if (added.Count > 0) {
            sb.AppendLine("新增的公共成员：");
            foreach (string line in added) sb.AppendLine("  + " + line);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 定位基线文件：从测试程序集所在目录向上找到解决方案根。
    /// </summary>
    /// <remarks>
    /// 基线必须落在源码树里（随代码一起评审），而不是 bin 输出目录。
    /// </remarks>
    private static string ResolveBaselinePath(string assemblyName) {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CommunicationKernel.slnx")))
            dir = dir.Parent;

        Assert.IsNotNull(dir, "未能从测试输出目录定位到解决方案根目录");
        return Path.Combine(dir!.FullName, "CommunicationKernel.Tests", "ApiBaselines", $"{assemblyName}.txt");
    }

    // =========================================================================
    // 渲染
    // =========================================================================

    /// <summary>
    /// 把程序集的公共可见面渲染成稳定、可比对的文本。
    /// </summary>
    /// <remarks>
    /// 只收录外部消费者真正能触达的成员：public 与 protected。
    /// 私有与 internal 成员是实现细节，改动不构成破坏性变更，
    /// 纳入只会让基线在每次内部重构时都产生噪声 diff。
    /// 全程按序数排序，保证不同机器上渲染结果一致。
    /// </remarks>
    private static string RenderPublicSurface(Assembly assembly) {
        var lines = new List<string>();

        foreach (Type type in assembly.GetExportedTypes()) {
            // 跳过工具生成的类型（Protobuf 的消息与 stub）。
            // 它们的形状完全由 Protos/V1/engine_host.proto 决定，
            // 而那份契约是单一来源且另有 CI 作业守着；
            // 收进来只会让基线里 600 行生成代码淹没掉十几行手写 API。
            if (IsGenerated(type)) continue;

            lines.Add(DescribeType(type));

            foreach (MemberInfo member in type.GetMembers(
                         BindingFlags.Public | BindingFlags.NonPublic |
                         BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)) {

                if (!IsExternallyVisible(member)) continue;

                // 属性与事件的访问器会以方法形式重复出现，跳过以免基线冗余
                if (member is MethodInfo { IsSpecialName: true }) continue;

                // 成员行必须带上所属类型：全局排序后，
                // 光一行 ".ctor()" 根本看不出是谁的构造函数，diff 也就失去意义
                lines.Add($"    {type.FullName}.{DescribeMember(member)}");
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>Protobuf 生成代码所在的命名空间（proto 的 csharp_namespace）。</summary>
    private const string GeneratedProtoNamespace = "CommunicationKernel.EngineHost.Grpc.V1";

    /// <summary>该类型（或其外层类型）是否由工具生成。</summary>
    /// <remarks>
    /// 除了看 <see cref="System.CodeDom.Compiler.GeneratedCodeAttribute"/>，
    /// 还必须按命名空间判断：protoc 生成的消息类型并不带该特性，
    /// 只靠特性会漏掉五百多个类型，把手写 API 淹没在生成代码里。
    /// </remarks>
    private static bool IsGenerated(Type type) {
        if (type.Namespace is not null &&
            (type.Namespace == GeneratedProtoNamespace ||
             type.Namespace.StartsWith(GeneratedProtoNamespace + ".", StringComparison.Ordinal)))
            return true;

        for (Type? t = type; t is not null; t = t.DeclaringType) {
            if (t.GetCustomAttribute<System.CodeDom.Compiler.GeneratedCodeAttribute>() is not null)
                return true;
        }
        return false;
    }

    private static bool IsExternallyVisible(MemberInfo member) => member switch {
        MethodBase m      => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly,
        FieldInfo f       => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly,
        PropertyInfo p    => IsExternallyVisible(p.GetMethod ?? p.SetMethod!),
        EventInfo e       => e.AddMethod is not null && IsExternallyVisible(e.AddMethod),
        Type t            => t.IsPublic || t.IsNestedPublic || t.IsNestedFamily,
        _                 => false
    };

    private static string DescribeType(Type type) {
        string kind = type.IsInterface ? "interface"
                    : type.IsEnum      ? "enum"
                    : type.IsValueType ? "struct"
                    : "class";

        string modifiers = type.IsAbstract && type.IsSealed ? " static"
                         : type.IsAbstract && !type.IsInterface ? " abstract"
                         : type.IsSealed && !type.IsValueType && !type.IsEnum ? " sealed"
                         : string.Empty;

        return $"{kind}{modifiers} {type.FullName}";
    }

    private static string DescribeMember(MemberInfo member) => member switch {
        ConstructorInfo c => $".ctor({FormatParameters(c.GetParameters())})",
        MethodInfo m      => $"{m.Name}({FormatParameters(m.GetParameters())}) -> {TypeName(m.ReturnType)}",
        PropertyInfo p    => $"{p.Name} {{ {(p.CanRead ? "get; " : "")}{(p.CanWrite ? "set; " : "")}}} -> {TypeName(p.PropertyType)}",
        FieldInfo f       => $"{f.Name} : {TypeName(f.FieldType)}",
        EventInfo e       => $"event {e.Name} : {TypeName(e.EventHandlerType!)}",
        _                 => member.Name
    };

    private static string FormatParameters(ParameterInfo[] parameters)
        => string.Join(", ", parameters.Select(p => $"{TypeName(p.ParameterType)} {p.Name}"));

    private static string TypeName(Type type) {
        if (!type.IsGenericType) return type.FullName ?? type.Name;

        string baseName = type.GetGenericTypeDefinition().FullName ?? type.Name;
        int tick = baseName.IndexOf('`');
        if (tick >= 0) baseName = baseName[..tick];

        return $"{baseName}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>";
    }
}
