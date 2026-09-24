// 客户端激活流程：
//   1. 首次运行：让用户输入激活码 -> 调用服务器 /api/activate -> 拿到令牌，本地缓存(含激活码本身)
//   2. 之后每次启动：优先联网调用 /api/heartbeat 换取新令牌（顺便更新服务器上的"最后心跳时间"，
//      管理员在后台就能看到这台设备是不是还活着）
//   3. 如果联网失败（用户暂时没网）：退化成校验"上次缓存的令牌"有没有过期（离线宽限期，
//      令牌有效期是服务器决定的，默认5天，也就是最多断网5天还能用，超过要求必须联网一次）
//   4. 服务器只要返回明确的拒绝（吊销/过期/设备不存在），无论如何都不能继续运行——
//      这种"硬拒绝"不受离线宽限期保护，避免用户靠一直断网躲避吊销

using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace ClientApp;

public record ActivationResult(bool Ok, string Reason, string? ModelKeyBase64, string? LicenseCode, string? ProductName);

public class OnlineLicenseClient
{
    private readonly string _serverBaseUrl;
    private readonly string _stateFilePath = Path.Combine(AppContext.BaseDirectory, "activation_state.bin");

    public OnlineLicenseClient(string serverBaseUrl) => _serverBaseUrl = serverBaseUrl.TrimEnd('/');

    public async Task<ActivationResult> EnsureActivatedAsync()
    {
        var machineId = MachineFingerprint.Get();
        var cached = ReadCachedState();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        var jsonOpts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        string? code = cached?.Code;
        if (code is null)
        {
            Console.Write("首次运行，请输入激活码: ");
            code = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(code))
                return new ActivationResult(false, "未输入激活码", null, null, null);
        }

        // 优先尝试联网（首次用 activate 接口，之后都用 heartbeat 接口，逻辑一致只是路径不同）
        var endpoint = cached is null ? "/api/activate" : "/api/heartbeat";
        try
        {
            var resp = await http.PostAsJsonAsync($"{_serverBaseUrl}{endpoint}",
                new { code, machineId, machineName = Environment.MachineName });

            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadFromJsonAsync<TokenResponseDto>(jsonOpts);
                var claims = ParseClaimsWithoutValidation(body!.Token);
                SaveCachedState(new CachedState(code, body.Token, body.ExpiresUtc));
                return new ActivationResult(true, "OK", claims["modelKey"], claims["license"], claims["product"]);
            }

            // 服务器明确拒绝（吊销/过期/设备不存在等）——这是"硬拒绝"，直接返回失败，
            // 不允许退化到离线缓存，否则吊销形同虚设
            var error = await resp.Content.ReadFromJsonAsync<ErrorDto>(jsonOpts);
            return new ActivationResult(false, error?.Error ?? $"服务器拒绝（HTTP {(int)resp.StatusCode}）", null, null, null);
        }
        catch (Exception) when (cached is not null)
        {
            // 联网失败（断网/服务器不可达），走离线宽限期：校验本地缓存令牌的签名与有效期
            var pubKeyPem = await TryGetCachedPublicKeyAsync(http);
            var offline = ValidateCachedTokenOffline(cached, pubKeyPem);
            if (offline.Ok) Console.WriteLine("（当前离线，正在使用本地缓存的授权，请尽快连接网络续期）");
            return offline;
        }
        catch (Exception ex)
        {
            return new ActivationResult(false, $"无法连接授权服务器且没有可用的本地缓存: {ex.Message}", null, null, null);
        }
    }

    private ActivationResult ValidateCachedTokenOffline(CachedState cached, string? publicKeyPem)
    {
        if (DateTime.UtcNow > cached.ExpiresUtc)
            return new ActivationResult(false, "本地缓存的授权已过期，必须联网重新验证", null, null, null);

        if (publicKeyPem is null)
        {
            // 完全拿不到公钥（也没网络），退而求其次只信任本地过期时间判断（弱校验，仅用于短暂断网场景）
            var claims = ParseClaimsWithoutValidation(cached.Token);
            return new ActivationResult(true, "OK(离线-未验签)", claims["modelKey"], claims["license"], claims["product"]);
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            var handler = new JwtSecurityTokenHandler();
            var parameters = new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = true,
                IssuerSigningKey = new RsaSecurityKey(rsa)
            };
            var principal = handler.ValidateToken(cached.Token, parameters, out _);
            string Get(string type) => principal.Claims.First(c => c.Type == type).Value;
            return new ActivationResult(true, "OK(离线)", Get("modelKey"), Get("license"), Get("product"));
        }
        catch (Exception ex)
        {
            return new ActivationResult(false, $"本地缓存的授权验证失败: {ex.Message}", null, null, null);
        }
    }

    private async Task<string?> TryGetCachedPublicKeyAsync(HttpClient http)
    {
        var cachedPubKeyPath = Path.Combine(AppContext.BaseDirectory, "server_public_key.pem");
        try
        {
            var resp = await http.GetAsync($"{_serverBaseUrl}/api/public-key");
            if (resp.IsSuccessStatusCode)
            {
                var pem = await resp.Content.ReadAsStringAsync();
                File.WriteAllText(cachedPubKeyPath, pem); // 顺便缓存一份，供真正离线时使用
                return pem;
            }
        }
        catch { /* 忽略，走下面的本地缓存兜底 */ }

        return File.Exists(cachedPubKeyPath) ? File.ReadAllText(cachedPubKeyPath) : null;
    }

    private static Dictionary<string, string> ParseClaimsWithoutValidation(string jwt)
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(jwt);
        return token.Claims.ToDictionary(c => c.Type, c => c.Value);
    }

    // ---- 本地状态缓存：用 DPAPI 加密存盘，防止普通用户直接改文件伪造已激活状态 ----
    private record CachedState(string Code, string Token, DateTime ExpiresUtc);

    private void SaveCachedState(CachedState state)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(state);
        var plain = System.Text.Encoding.UTF8.GetBytes(json);
        var protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_stateFilePath, protectedBytes);
    }

    private CachedState? ReadCachedState()
    {
        if (!File.Exists(_stateFilePath)) return null;
        try
        {
            var protectedBytes = File.ReadAllBytes(_stateFilePath);
            var plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return System.Text.Json.JsonSerializer.Deserialize<CachedState>(plain);
        }
        catch { return null; }
    }

    private record TokenResponseDto(string Token, DateTime ExpiresUtc);
    private record ErrorDto(string? Error);
}
