// -----------------------------------------------------------------------------
// 文件: Services/WebAuthEndpoints.cs
// 层级: UI 层 — WebMaster 访问控制
// 作用: 登录 / 登出端点，以及登录页的 HTML。
//
// 为什么用最小 API 而不是 Blazor 组件:
//   登录要写 Cookie，而 Cookie 只能在<b>普通 HTTP 响应</b>里下发。
//   Blazor Server 的交互组件跑在 SignalR 线路上，那时响应头早就发完了，
//   在组件里调 SignAsync 会抛"响应已开始"。
//   用最小 API + 一个纯 HTML 表单，绕开整个渲染模式问题，
//   也保证登录页在线路建立<b>之前</b>就能显示——否则未登录时连页面都出不来。
//
// 作用范围:
//   认证只加在 Blazor 端点上（见 Program.cs 的 RequireAuthorization）。
//   gRPC 的 :5000 <b>不受影响</b>——WPF 与本进程回环调用都不带 Cookie，
//   给它加认证会让上位机直接连不上引擎。
//   那个口的隔离靠不对外暴露，见部署文档。
// -----------------------------------------------------------------------------

using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace CommunicationKernel.UI.WebMaster.Services;

/// <summary>登录相关的端点与页面。</summary>
public static class WebAuthEndpoints
{
    /// <summary>登录页与登出的路径，Program.cs 与本类共用。</summary>
    public const string LoginPath = "/login";

    /// <summary>登出路径。</summary>
    public const string LogoutPath = "/logout";

    /// <summary>
    /// 口令错误时的固定延迟（毫秒）。
    /// </summary>
    /// <remarks>
    /// 不做完整的限流，只加一个固定延迟：把每秒可试次数从上千压到几次，
    /// 对在线爆破已经足够，也不会因为记状态而引入新的复杂度。
    /// 固定值而非递增——递增需要按来源记账，而来源在反代后面全是同一个 IP。
    /// </remarks>
    private const int FailureDelayMs = 800;

    /// <summary>注册 /login 与 /logout。</summary>
    /// <param name="app">应用。</param>
    public static void MapAuthEndpoints(this WebApplication app)
    {
        // 登录页本身必须允许匿名，否则会重定向到自己形成死循环
        app.MapGet(LoginPath, (HttpContext ctx, string? returnUrl, bool? failed) =>
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            return ctx.Response.WriteAsync(
                BuildLoginHtml(ctx.Request.PathBase, returnUrl, failed == true));
        }).AllowAnonymous();

        app.MapPost(LoginPath, async (HttpContext ctx, WebAuthStore auth) =>
        {
            IFormCollection form = await ctx.Request.ReadFormAsync().ConfigureAwait(false);
            string password = form["password"].ToString();
            string returnUrl = form["returnUrl"].ToString();

            if (!auth.Verify(password))
            {
                // 延迟后再回登录页。不提示"口令错误"以外的任何细节
                await Task.Delay(FailureDelayMs).ConfigureAwait(false);
                return Results.Redirect(
                    ctx.Request.PathBase + LoginPath + "?failed=true" + ReturnUrlQuery(returnUrl));
            }

            ClaimsPrincipal user = new(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, "operator") },
                CookieAuthenticationDefaults.AuthenticationScheme));

            await ctx.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                user,
                new AuthenticationProperties { IsPersistent = true }).ConfigureAwait(false);

            // 只接受本站内的相对路径，避免被构造成跳转到外部站点的钓鱼链接
            string target = IsSafeReturnUrl(returnUrl) ? returnUrl : "/";
            return Results.Redirect(ctx.Request.PathBase + target);
        }).AllowAnonymous().DisableAntiforgery();

        app.MapPost(LogoutPath, async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)
                     .ConfigureAwait(false);
            return Results.Redirect(ctx.Request.PathBase + LoginPath);
        }).DisableAntiforgery();
    }

    /// <summary>
    /// 判断回跳地址是否安全。
    /// </summary>
    /// <remarks>
    /// 只允许 <c>/xxx</c> 形式的站内相对路径。
    /// <c>//evil.com</c> 会被浏览器当成协议相对的<b>外部</b>地址，
    /// 因此必须连同它一起挡掉——这是开放重定向最常见的绕过写法。
    /// </remarks>
    private static bool IsSafeReturnUrl(string? url) =>
        !string.IsNullOrEmpty(url)
        && url.StartsWith('/')
        && !url.StartsWith("//", StringComparison.Ordinal);

    /// <summary>把回跳地址拼成查询串；不安全或为空时返回空串。</summary>
    private static string ReturnUrlQuery(string? returnUrl) =>
        IsSafeReturnUrl(returnUrl) ? "&returnUrl=" + Uri.EscapeDataString(returnUrl!) : string.Empty;

    /// <summary>
    /// 生成登录页 HTML。
    /// </summary>
    /// <remarks>
    /// 手写 HTML 而不是复用 Blazor 布局：登录页要在线路建立之前就能显示，
    /// 而且它是未认证用户唯一能看到的页面，越少依赖越不容易出问题。
    /// 样式复用 theme.css，观感与主界面一致。
    /// </remarks>
    private static string BuildLoginHtml(string pathBase, string? returnUrl, bool failed)
    {
        string action = pathBase + LoginPath;
        string hidden = IsSafeReturnUrl(returnUrl)
            ? $"<input type=\"hidden\" name=\"returnUrl\" value=\"{HtmlEncode(returnUrl!)}\" />"
            : string.Empty;

        string error = failed
            ? "<div class=\"status-msg status-error\" style=\"margin-bottom:12px\">口令不正确</div>"
            : string.Empty;

        StringBuilder sb = new();
        sb.Append("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\" />");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1, viewport-fit=cover\" />");
        sb.Append("<title>登录 — CommunicationKernel</title>");
        sb.Append($"<link rel=\"icon\" type=\"image/svg+xml\" href=\"{pathBase}/favicon.svg\" />");
        sb.Append($"<link rel=\"stylesheet\" href=\"{pathBase}/css/theme.css\" />");
        sb.Append("<style>");
        sb.Append("body{display:flex;align-items:center;justify-content:center;min-height:100dvh;margin:0;padding:16px}");
        sb.Append(".login-card{width:min(360px,100%)}");
        sb.Append(".login-card h1{font-size:16px;margin:0 0 4px}");
        sb.Append("</style></head><body>");
        sb.Append("<form class=\"settings-card login-card\" method=\"post\" action=\"").Append(action).Append("\">");
        sb.Append("<h1>CommunicationKernel</h1>");
        sb.Append("<p class=\"setting-hint\" style=\"margin-bottom:12px\">请输入访问口令</p>");
        sb.Append(error);
        sb.Append(hidden);
        sb.Append("<label>口令</label>");
        sb.Append("<input type=\"password\" name=\"password\" autofocus autocomplete=\"current-password\" />");
        sb.Append("<div class=\"form-actions\" style=\"margin-top:16px\">");
        sb.Append("<button class=\"btn primary\" type=\"submit\" style=\"width:100%\">登录</button>");
        sb.Append("</div>");
        sb.Append("<p class=\"setting-hint\" style=\"margin-top:16px\">");
        sb.Append("忘记口令：删掉 exe 旁 config/web-auth.json 即可恢复免登录。");
        sb.Append("</p>");
        sb.Append("</form></body></html>");

        return sb.ToString();
    }

    /// <summary>最小化的 HTML 转义，用于把回跳地址安全地放进 value 属性。</summary>
    private static string HtmlEncode(string s) =>
        s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
}
