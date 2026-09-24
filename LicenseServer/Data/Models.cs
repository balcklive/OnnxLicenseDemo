namespace LicenseServer.Data;

// 一张 LicenseKey = 客户购买的一份订阅/一个激活码。
// MaxActivations 控制"最多能在几台机器上同时激活"（比如允许1台，或3台）。
public class LicenseKey
{
    public int Id { get; set; }
    public string Code { get; set; } = "";           // 形如 ABCD-EFGH-1234-5678，客户输入这个来激活
    public string ProductName { get; set; } = "";
    public string ModelKeyBase64 { get; set; } = "";  // AES-256 模型密钥，激活成功后随token下发给客户端
    public int MaxActivations { get; set; } = 1;
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiryUtc { get; set; }           // 订阅到期时间，到期后无法再心跳续期
    public bool IsRevoked { get; set; }                // 管理员手动吊销整张license

    public List<Activation> Activations { get; set; } = new();
}

// 一条 Activation = 这张license在某一台具体机器上的激活记录
public class Activation
{
    public int Id { get; set; }
    public int LicenseKeyId { get; set; }
    public LicenseKey LicenseKey { get; set; } = null!;

    public string MachineId { get; set; } = "";
    public string? MachineName { get; set; }           // 客户端可选上报，方便管理员在后台辨认是哪台设备
    public DateTime ActivatedUtc { get; set; }
    public DateTime LastHeartbeatUtc { get; set; }
    public bool IsRevoked { get; set; }                // 管理员只踢掉这一台设备，腾出激活名额给别的机器
}
