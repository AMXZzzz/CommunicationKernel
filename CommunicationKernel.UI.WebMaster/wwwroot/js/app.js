// 文件: wwwroot/js/app.js
// 作用: 日志区滚到底；按 UA / 屏宽给 html 打 is-phone。
// 华为/荣耀常忽略 viewport，按 ~980px 桌面排版，只认 max-width:720 会把 is-phone 又摘掉。
window.ck = {
    scrollToBottom: function (id) {
        var el = document.getElementById(id);
        if (el) el.scrollTop = el.scrollHeight;
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
