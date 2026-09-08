// -----------------------------------------------------------------------------
// 文件: Services/WebAuthStore.cs
// 层级: UI 层 — WebMaster 访问控制
// 作用: 一个口令的登录门槛。设了口令才启用，没设就完全不拦（本机/局域网照旧）。
//
// 定位（务必先读）:
//   这是<b>一道薄门槛</b>，不是完整的身份体系：没有用户、没有权限分级、
//   没有审计。它挡的是"网址被扫到就直接进来改 PLC"这一类。
//   真正暴露到公网时，反向代理上的 Basic Auth / OAuth / IP 白名单仍然要加——
//   两者是叠加关系，不是二选一。
//
// 口令怎么存:
//   PBKDF2-HMACSHA256，16 字节随机盐，10 万次迭代，存 Base64。
//   绝不存明文，也不用 MD5/SHA1 直接哈希——那两种在显卡上每秒能试上亿次，
//   而工厂里的口令通常很短。
// -----------------------------------------------------------------------------

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunicationKernel.Hosting.Sdk;

namespace CommunicationKernel.UI.WebMaster.Services;

/// <summary>已保存的登录口令凭据。</summary>
/// <param name="Salt">Base64 编码的随机盐。</param>
/// <param name="Hash">Base64 编码的 PBKDF2 派生值。</param>
/// <param name="Iterations">迭代次数，随文件保存以便将来提高强度而不作废旧口令。</param>
public sealed record WebAuthCredential(string Salt, string Hash, int Iterations);

/// <summary>读写 exe 旁 <c>config/web-auth.json</c>，并校验口令。</summary>
public sealed class WebAuthStore
{
    /// <summary>PBKDF2 迭代次数。</summary>
    /// <remarks>
    /// 10 万次在树莓派上约几十毫秒，登录时用户无感；
    /// 对暴力破解来说则是每次尝试都要付出同样的代价。
    /// </remarks>
    private const int Iterations = 100_000;

    /// <summary>盐长度（字节）。</summary>
    private const int SaltBytes = 16;

    /// <summary>派生密钥长度（字节）。</summary>
    private const int HashBytes = 32;

    /// <summary>口令最短长度。</summary>
    /// <remarks>
    /// 4 位以下几乎等同于没有。这里不强制复杂度——现场是工人在手机上输，
    /// 强制大小写加符号只会让人写在设备旁边的纸上，反而更糟。
    /// </remarks>
    public const int MinPasswordLength = 4;

    /// <summary>保护写入的互斥锁。</summary>
    private readonly object _lock = new();

    /// <summary>应用日志。</summary>
    private readonly AppLogStore _log;

    /// <param name="log">应用日志，用于记录口令变更与登录失败。</param>
    public WebAuthStore(AppLogStore log) => _log = log;

    /// <summary>配置文件完整路径，供设置页展示。</summary>
    public string FilePath => WebPaths.AuthFile;

    /// <summary>当前是否已设口令（即是否启用登录）。</summary>
    public bool IsEnabled => Load() is not null;

    /// <summary>
    /// 读取已保存的凭据；未设口令或文件损坏时返回 null。
    /// </summary>
    /// <remarks>
    /// <b>静态方法</b>：Program.cs 要在 DI 容器建好之前就据此决定装不装认证中间件。
    /// <para>
    /// 文件损坏时返回 null＝不启用登录。这是刻意的取舍：
    /// 反过来（损坏就锁死）会让人被自己的配置文件关在门外，
    /// 而现场没有第二条路进去改。宁可退回"不设防"并在日志里喊一声。
    /// </para>
    /// </remarks>
    public static WebAuthCredential? Load()
    {
        try
        {
            string path = WebPaths.AuthFile;
            if (!File.Exists(path)) return null;

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;

            string? salt = root.TryGetProperty("Salt", out JsonElement s) ? s.GetString() : null;
            string? hash = root.TryGetProperty("Hash", out JsonElement h) ? h.GetString() : null;
            int iter = root.TryGetProperty("Iterations", out JsonElement i) && i.TryGetInt32(out int v)
                ? v : Iterations;

            if (string.IsNullOrWhiteSpace(salt) || string.IsNullOrWhiteSpace(hash))
                return null;

            return new WebAuthCredential(salt, hash, iter);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 校验口令。
    /// </summary>
    /// <returns>未设口令时一律返回 true（等于没有门槛）。</returns>
    /// <remarks>
    /// 比较用 <see cref="CryptographicOperations.FixedTimeEquals"/> 而非 <c>==</c>：
    /// 普通比较在第一个不同的字节就返回，攻击者可以按响应时间逐字节猜出哈希。
    /// 这条链路上时间差很小，但成本也几乎为零，没有理由不用。
    /// </remarks>
    public bool Verify(string? password)
    {
        WebAuthCredential? cred = Load();
        if (cred is null) return true;

        if (string.IsNullOrEmpty(password)) return false;

        try
        {
            byte[] salt = Convert.FromBase64String(cred.Salt);
            byte[] expected = Convert.FromBase64String(cred.Hash);

            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password), salt, cred.Iterations,
                HashAlgorithmName.SHA256, expected.Length);

            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            // 凭据文件被改坏（Base64 非法等）：按校验失败处理，不放行
            return false;
        }
    }

    /// <summary>
    /// 设置或修改口令。
    /// </summary>
    /// <exception cref="ArgumentException">口令过短。</exception>
    /// <exception cref="InvalidOperationException">落盘失败。</exception>
    public void SetPassword(string password)
    {
        if (password is null || password.Length < MinPasswordLength)
            throw new ArgumentException($"口令至少 {MinPasswordLength} 位。", nameof(password));

        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations,
            HashAlgorithmName.SHA256, HashBytes);

        lock (_lock)
        {
            var payload = new
            {
                Salt = Convert.ToBase64String(salt),
                Hash = Convert.ToBase64String(hash),
                Iterations,
            };

            if (!JsonFileStore.SaveObject(WebPaths.AuthFile, payload, out string error))
            {
                _log.Warn("Auth", "保存口令失败: " + error);
                throw new InvalidOperationException("保存口令失败: " + error);
            }
        }

        // 只记"改过"，绝不记口令本身——日志页是任何登录用户都能看的
        _log.Info("Auth", "已设置登录口令，下次访问需要登录");
    }

    /// <summary>
    /// 清除口令，恢复为不需要登录。
    /// </summary>
    /// <remarks>
    /// 删文件而不是写一个空口令：留着空凭据会让 <see cref="Load"/> 的判断
    /// 多一种中间状态，而"文件在不在"是最不容易搞错的开关。
    /// </remarks>
    public void ClearPassword()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(WebPaths.AuthFile))
                    File.Delete(WebPaths.AuthFile);
            }
            catch (Exception ex)
            {
                _log.Warn("Auth", "清除口令失败: " + ex.Message);
                throw new InvalidOperationException("清除口令失败: " + ex.Message);
            }
        }

        _log.Warn("Auth", "已清除登录口令，任何人都可直接访问");
    }
}
