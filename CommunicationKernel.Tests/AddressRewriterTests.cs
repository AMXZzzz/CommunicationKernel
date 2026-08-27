// -----------------------------------------------------------------------------
// 文件: AddressRewriterTests.cs
// 层级: 测试
// 作用: 锁住批量改地址的计算规则。
//
// 为什么值得测:
//   查找串与正则由操作员在现场输入，边角情况全是用户能踩到的：
//   空查找、写错的正则、引用了不存在的捕获组、灾难性回溯。
//   这些若不拦住，轻则界面卡死，重则把整张变量表的地址搅烂——
//   而地址搅烂之后没有撤销，只能靠导出的备份恢复。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using CommunicationKernel.Hosting.Sdk;

namespace CommunicationKernel.Tests;

[TestClass]
public class AddressRewriterTests {

    /// <summary>构造一组典型的 Modbus 地址候选。</summary>
    private static List<AddressCandidate> Sample() => new() {
        new AddressCandidate("1", "传送带速度", "40001"),
        new AddressCandidate("2", "电机温度",   "40002"),
        new AddressCandidate("3", "运行状态",   "coil:5"),
    };

    // =========================================================================
    // 普通替换
    // =========================================================================

    [TestMethod]
    public void Plain_ReplacesMatchingOnly() {
        bool ok = AddressRewriter.TryPreview(
            Sample(), "4000", "DB1.DBW", useRegex: false,
            out IReadOnlyList<AddressRewrite> changes, out string error);

        Assert.IsTrue(ok, error);
        // coil:5 不含 "4000"，不应出现在结果里
        Assert.HasCount(2, changes);
        Assert.AreEqual("DB1.DBW1", changes[0].NewAddress);
        Assert.AreEqual("DB1.DBW2", changes[1].NewAddress);
    }

    [TestMethod]
    public void Plain_UnchangedEntriesAreExcluded() {
        // 把 40001 换成它自己：结果相同，不算改动
        bool ok = AddressRewriter.TryPreview(
            Sample(), "40001", "40001", useRegex: false,
            out IReadOnlyList<AddressRewrite> changes, out _);

        Assert.IsTrue(ok);
        Assert.IsEmpty(changes, "替换后没有变化的条目不应进入预览，否则会淹没真正的改动");
    }

    [TestMethod]
    public void Plain_NoMatch_SucceedsWithEmptyResult() {
        // 「没匹配到」是成功但无变化，不是失败——两者对用户的含义完全不同
        bool ok = AddressRewriter.TryPreview(
            Sample(), "DB99", "X", useRegex: false,
            out IReadOnlyList<AddressRewrite> changes, out string error);

        Assert.IsTrue(ok, "没有匹配项不是错误");
        Assert.IsNull(error);
        Assert.IsEmpty(changes);
    }

    [TestMethod]
    public void Plain_EmptyReplacement_DeletesMatchedPart() {
        bool ok = AddressRewriter.TryPreview(
            Sample(), "coil:", string.Empty, useRegex: false,
            out IReadOnlyList<AddressRewrite> changes, out _);

        Assert.IsTrue(ok);
        Assert.HasCount(1, changes);
        Assert.AreEqual("5", changes[0].NewAddress);
    }

    // =========================================================================
    // 正则
    // =========================================================================

    [TestMethod]
    public void Regex_CaptureGroupIsUsable() {
        // 换 PLC 型号的典型用法：把 4000N 映射成 DB1.DBWN
        bool ok = AddressRewriter.TryPreview(
            Sample(), @"^4000(\d)$", "DB1.DBW$1", useRegex: true,
            out IReadOnlyList<AddressRewrite> changes, out string error);

        Assert.IsTrue(ok, error);
        Assert.HasCount(2, changes);
        Assert.AreEqual("DB1.DBW1", changes[0].NewAddress);
        Assert.AreEqual("DB1.DBW2", changes[1].NewAddress);
    }

    [TestMethod]
    public void Regex_Invalid_FailsWithReason() {
        // 未闭合的分组
        bool ok = AddressRewriter.TryPreview(
            Sample(), "^(4000", "X", useRegex: true,
            out IReadOnlyList<AddressRewrite> changes, out string error);

        Assert.IsFalse(ok, "语法错误的正则必须失败，不能静默当成字面量去替换");
        Assert.IsNotNull(error);
        StringAssert.Contains(error, "正则");
        Assert.IsEmpty(changes);
    }

    [TestMethod]
    public void Regex_AnchoredPattern_DoesNotTouchOthers() {
        bool ok = AddressRewriter.TryPreview(
            Sample(), @"^coil:(\d+)$", "Q$1", useRegex: true,
            out IReadOnlyList<AddressRewrite> changes, out _);

        Assert.IsTrue(ok);
        Assert.HasCount(1, changes);
        Assert.AreEqual("Q5", changes[0].NewAddress);
        Assert.AreEqual("3", changes[0].Id, "改写结果必须能按 Id 回填到正确的变量");
    }

    // =========================================================================
    // 防御
    // =========================================================================

    [TestMethod]
    public void EmptyFind_IsRejected() {
        // 空查找：普通替换会在每个字符间插入，正则会匹配所有空位置，两种都是把地址搅烂
        bool ok = AddressRewriter.TryPreview(
            Sample(), string.Empty, "X", useRegex: false,
            out IReadOnlyList<AddressRewrite> changes, out string error);

        Assert.IsFalse(ok);
        Assert.IsNotNull(error);
        Assert.IsEmpty(changes);
    }

    [TestMethod]
    public void NullCandidates_ReturnsEmptyWithoutThrowing() {
        bool ok = AddressRewriter.TryPreview(
            null, "4", "5", useRegex: false,
            out IReadOnlyList<AddressRewrite> changes, out string error);

        Assert.IsTrue(ok, error);
        Assert.IsEmpty(changes);
    }

    [TestMethod]
    public void ChangesNeverNull_EvenOnFailure() {
        AddressRewriter.TryPreview(
            Sample(), "(", "x", useRegex: true,
            out IReadOnlyList<AddressRewrite> changes, out _);

        // 调用方会直接 .Count，返回 null 会在错误路径上再叠一个空引用
        Assert.IsNotNull(changes);
    }

    [TestMethod]
    public void Regex_CatastrophicBacktracking_TimesOutInsteadOfHanging() {
        // (a+)+$ 配上一串不匹配的 a，是灾难性回溯的经典构造。
        // 没有超时保护时这一句会把线程钉死，且看不出是正则的问题。
        List<AddressCandidate> evil = new() {
            new AddressCandidate("1", "恶意", new string('a', 40) + "!"),
        };

        bool ok = AddressRewriter.TryPreview(
            evil, "(a+)+$", "X", useRegex: true,
            out IReadOnlyList<AddressRewrite> changes, out string error);

        // 关键是"没有挂住"：要么正常算完，要么被超时拦下并给出说明
        if (!ok) {
            Assert.IsNotNull(error);
            Assert.IsEmpty(changes, "超时属于整批中止，不能留下改了一半的结果");
        }
    }

    [TestMethod]
    public void NullAddress_IsTreatedAsEmpty() {
        List<AddressCandidate> withNull = new() {
            new AddressCandidate("1", "缺地址", null!),
        };

        bool ok = AddressRewriter.TryPreview(
            withNull, "x", "y", useRegex: false,
            out IReadOnlyList<AddressRewrite> changes, out string error);

        Assert.IsTrue(ok, error);
        Assert.IsEmpty(changes, "空地址替换后仍为空，不算改动");
    }
}
