// 文件: wwwroot/js/app.js
// 作用: 日志区滚到底；按 UA / 屏宽给 html 打 is-phone。
// 华为/荣耀常忽略 viewport，按 ~980px 桌面排版，只认 max-width:720 会把 is-phone 又摘掉。
window.ck = {
    scrollToBottom: function (id) {
        var el = document.getElementById(id);
        if (el) el.scrollTop = el.scrollHeight;
    },

    // 把文本直接存成文件。
    //
    // Blazor Server 没有服务端文件对话框，唯一的办法是在浏览器里造一个
    // Blob 再触发一次 <a download> 点击。
    //
    // 用 Blob 而不是 data: URI：Chrome 对 data: URI 的 <a download> 有长度上限，
    // 几百个变量的 JSON 会被静默截断——下下来的文件是坏的，还看不出来。
    //
    // URL.revokeObjectURL 必须调，否则每导出一次就在内存里挂一份副本，
    // 页面不刷新就不会释放。setTimeout 是因为部分浏览器在同步 revoke 时
    // 下载还没真正开始，会得到一个空文件。
    downloadText: function (fileName, text) {
        var blob = new Blob([text], { type: "application/json;charset=utf-8" });
        var url = URL.createObjectURL(blob);
        var a = document.createElement("a");
        a.href = url;
        a.download = fileName;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
    },

    // 复制到剪贴板。navigator.clipboard 在非 HTTPS 且非 localhost 下不存在
    // （车间用 http://内网IP 访问时正是这种情况），因此必须保留 execCommand 兜底。
    copyText: function (text) {
        if (navigator.clipboard && window.isSecureContext) {
            navigator.clipboard.writeText(text);
            return true;
        }
        var ta = document.createElement("textarea");
        ta.value = text;
        ta.style.position = "fixed";
        ta.style.opacity = "0";
        document.body.appendChild(ta);
        ta.select();
        var ok = false;
        try { ok = document.execCommand("copy"); } catch (e) { ok = false; }
        document.body.removeChild(ta);
        return ok;
    }
};

(function () {
    // is-phone 只解决一件事：部分浏览器（华为/荣耀等）忽略 <meta viewport>，
    // 按 ~980px 桌面宽排版，于是 CSS 的 max-width:900px 永远不匹配，
    // 手机上却拿到了桌面布局。那种情况只能靠 UA 兜底。
    //
    // 其余一律交给 CSS 媒体查询：视口多宽就用多宽的布局。
    // 窄窗口的桌面浏览器由 @media (max-width: 900px) 覆盖，不需要这个类。
    //
    // 【不要再按触摸能力或 screen 尺寸判断】——曾经这么写过，结果是
    // 触摸屏笔记本上的桌面浏览器被判成手机：
    //   · maxTouchPoints > 0 在大量 Windows 笔记本上都成立（触摸屏、手写笔、部分触控板）；
    //   · screen.width/height 会被系统缩放比例缩小，2880×1800 @200% 报成 1440×900，
    //     于是 "minSide <= 920" 命中，窗口明明有 1900px 宽也被塞进手机布局。
    function isPhone() {
        var ua = navigator.userAgent || "";
        return /Android|iPhone|iPod|iPad|Mobile|Huawei|Harmony|HMSCore|MicroMessenger/i.test(ua);
    }

    function sync() {
        document.documentElement.classList.toggle("is-phone", isPhone());
    }

    sync();
    window.addEventListener("resize", sync);
    window.addEventListener("orientationchange", sync);
})();
