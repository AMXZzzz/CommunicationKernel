// 文件: wwwroot/js/app.js
// 作用: 日志区滚到底；按宽度/触摸自动给 html 打 is-phone，CSS 据此切换布局。
window.ck = {
    scrollToBottom: function (id) {
        var el = document.getElementById(id);
        if (el) el.scrollTop = el.scrollHeight;
    }
};

(function () {
    function isPhone() {
        return window.matchMedia("(max-width: 720px)").matches
            || (window.matchMedia("(pointer: coarse)").matches
                && window.matchMedia("(max-width: 900px)").matches);
    }

    function sync() {
        document.documentElement.classList.toggle("is-phone", isPhone());
    }

    sync();
    window.addEventListener("resize", sync);
    window.addEventListener("orientationchange", sync);
    var q = window.matchMedia("(max-width: 720px)");
    if (q.addEventListener) q.addEventListener("change", sync);
})();
