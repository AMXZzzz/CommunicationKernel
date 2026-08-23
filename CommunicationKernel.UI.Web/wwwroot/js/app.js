// 文件: wwwroot/js/app.js
// 作用: 日志区滚到底；不引入任何第三方库。
window.ck = {
    scrollToBottom: function (id) {
        var el = document.getElementById(id);
        if (el) el.scrollTop = el.scrollHeight;
    }
};
