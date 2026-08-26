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
    function isPhone() {
        var ua = navigator.userAgent || "";
        if (/Android|iPhone|iPod|Mobile|Huawei|Harmony|HMSCore|MicroMessenger/i.test(ua))
            return true;
        var minSide = Math.min(screen.width || 9999, screen.height || 9999);
        if ((navigator.maxTouchPoints || 0) > 0 && minSide <= 920)
            return true;
        return window.matchMedia("(max-width: 900px)").matches
            || (window.matchMedia("(pointer: coarse)").matches
                && window.matchMedia("(max-width: 1100px)").matches);
    }

    function sync() {
        document.documentElement.classList.toggle("is-phone", isPhone());
    }

    sync();
    window.addEventListener("resize", sync);
    window.addEventListener("orientationchange", sync);
})();
