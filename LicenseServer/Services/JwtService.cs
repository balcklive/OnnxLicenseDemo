using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace LicenseServer.Services;

// 服务端用 RSA 私钥给下发给客户端的"激活令牌"签名（RS256）。
// 客户端只持有公钥，可以在没有网络的情况下本地验证令牌是否被篡改/是否过期（离线宽限期用）。
//
// 密钥文件路径通过环境变量 KEYS_DIR 指定，默认 ./keys。
// 首次启动如果目录里没有密钥，会自动生成一份 —— 但 Docker 部署时请把这个目录挂载成
// 持久化 volume，否则容器重启就换了新密钥，所有已经发出去的客户端令牌全部作废。
public class JwtService
{
    private readonly RSA _rsa;
    public string Issuer { get; } = "LicenseServer";

    public JwtService(IConfiguration config)
    {
        var keysDir = config["KEYS_DIR"] ?? "./keys";
        Directory.CreateDirectory(keysDir);
        var privatePath = Path.Combine(keysDir, "private_key.pem");
        var publicPath = Path.Combine(keysDir, "public_key.pem");

        _rsa = RSA.Create(2048);
        if (File.Exists(privatePath))
        {
            _rsa.ImportFromPem(File.ReadAllText(privatePath));
        }
        else
        {
            File.WriteAllText(privatePath, _rsa.ExportRSAPrivateKeyPem());
            File.WriteAllText(publicPath, _rsa.ExportRSAPublicKeyPem());
        }
    }

    public string GetPublicKeyPem() => _rsa.ExportRSAPublicKeyPem();

    // 签发令牌：把机器号、许可证号、模型密钥等塞进 claims 里签名。
    // validFor 建议设短一点（比如3~7天），逼客户端定期回来心跳续期，
    // 这样管理员吊销授权后，最多等一个validFor周期客户端就会失效，而不是永久有效。
    //
    // subscriptionExpiresUtc = 这张证的订阅到期（客户买的时长）。**和 validFor/exp 是两件事**：
    // exp 只是离线宽限期，每次心跳都重置回 validFor，拿它当"剩余天数"会让客户天天看到同一个数。
    // 客户端顶栏的"授权 剩余 N 天"只认这个 claim；不给（null）时客户端显示"天数未知"占位。
    public (string token, DateTime expiresUtc) IssueToken(
        string machineId, string licenseCode, string productName, string modelKeyBase64, TimeSpan validFor,
        DateTime? subscriptionExpiresUtc = null)
    {
        var expires = DateTime.UtcNow.Add(validFor);
        var claims = new List<Claim>
        {
            new Claim("machine", machineId),
            new Claim("license", licenseCode),
            new Claim("product", productName),
            new Claim("modelKey", modelKeyBase64),
        };

        // unix 秒，与 exp 同格式。显式 SpecifyKind：Npgsql 读回来的 timestamptz 可能是
        // Kind=Unspecified，而 DateTimeOffset 对 Unspecified 会**按本机时区**解释，
        // 那会让客户看到的天数整体偏一个时区（北京时间差 8 小时，跨零点就差一天）。
        if (subscriptionExpiresUtc is { } subscription)
            claims.Add(new Claim("subExp",
                new DateTimeOffset(DateTime.SpecifyKind(subscription, DateTimeKind.Utc)).ToUnixTimeSeconds()
                    .ToString(CultureInfo.InvariantCulture)));

        var credentials = new SigningCredentials(new RsaSecurityKey(_rsa), SecurityAlgorithms.RsaSha256);
        var token = new JwtSecurityToken(
            issuer: Issuer,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expires,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
