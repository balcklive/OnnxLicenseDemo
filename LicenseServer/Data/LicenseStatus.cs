namespace LicenseServer.Data;

// 许可证"有效性"状态。注意它与"有没有设备激活"是两件独立的事：
// 一张证可以状态=正常 但 一台设备都没激活。所以激活与否单独用 IsActivated() 判断，
// 不塞进这个枚举里，避免出现"已激活"和"已过期"争抢同一个槽位的语义混乱。
public enum LicenseState
{
    Active,        // 正常
    ExpiringSoon,  // 即将到期
    Expired,       // 已过期
    Revoked        // 已吊销
}

// 列表筛选用的档位。比 LicenseState 多两档，因为筛选是"用户想看什么"，
// 而状态是"这张证是什么"。
public enum LicenseFilter
{
    All,          // 全部
    Active,       // 正常
    ExpiringSoon, // 即将到期
    Expired,      // 已过期
    Revoked,      // 已吊销
    Unactivated   // 未激活（没有任何有效设备）
}

// 状态语义的单一真相源。
// 放在 LicenseServer.Data 命名空间下是刻意的：_ViewImports.cshtml 已经 @using 了这个命名空间，
// 视图里可以直接调用扩展方法，不必再动那个文件。
public static class LicenseStatus
{
    public const int ExpiringSoonDays = 30;  // 距到期 ≤30 天算"即将到期"
    public const int CriticalExpiryDays = 7; // 距到期 ≤7 天，大屏单独预警
    public const int StaleHeartbeatDays = 7; // 超过 7 天没心跳，疑似客户已弃用

    // 判定优先级：吊销 > 过期 > 即将到期 > 正常。
    // 吊销优先是刻意的——吊销是管理员的主动动作，也是更"新"的事实：
    // 一张被吊销的证即使后来过期了，管理员该看到的仍然是"我吊销过它"。
    public static LicenseState State(this LicenseKey l, DateTime utcNow)
    {
        if (l.IsRevoked) return LicenseState.Revoked;
        if (utcNow > l.ExpiryUtc) return LicenseState.Expired;
        if (l.ExpiryUtc - utcNow <= TimeSpan.FromDays(ExpiringSoonDays)) return LicenseState.ExpiringSoon;
        return LicenseState.Active;
    }

    // 已用席位 = 未撤销的设备数。撤销某台设备就是"腾出名额"，见 README 的席位语义。
    public static int UsedSeats(this LicenseKey l) => l.Activations.Count(a => !a.IsRevoked);

    public static bool IsActivated(this LicenseKey l) => l.Activations.Any(a => !a.IsRevoked);

    public static bool IsSeatFull(this LicenseKey l) => l.UsedSeats() >= l.MaxActivations;

    public static bool IsStale(this Activation a, DateTime utcNow) =>
        !a.IsRevoked && utcNow - a.LastHeartbeatUtc > TimeSpan.FromDays(StaleHeartbeatDays);

    // 中文标签。与视图解耦，保证大屏、列表、徽章三处永远说的是同一个词。
    public static string Label(this LicenseState s) => s switch
    {
        LicenseState.Active => "正常",
        LicenseState.ExpiringSoon => "即将到期",
        LicenseState.Expired => "已过期",
        LicenseState.Revoked => "已吊销",
        _ => "未知"
    };

    // 语义色调槽位：ok / warn / critical / neutral。
    // "已吊销"刻意走 neutral 而不是又一个红色——吊销是管理员的主动操作，不是故障。
    // 另外调色板验证器测出 warning #fab219 与 serious #ec835a 的法向视觉 ΔE 仅 13.6（低于 15 底线），
    // 两色肉眼难分，所以全套设计只用 三 个语义色 + 中性，不引入第四个暖色。
    public static string Tone(this LicenseState s) => s switch
    {
        LicenseState.Active => "ok",
        LicenseState.ExpiringSoon => "warn",
        LicenseState.Expired => "critical",
        _ => "neutral"
    };

    // 对应图标精灵里的 <symbol id>。图标一律自绘 SVG，不用 Unicode 字符代替。
    public static string Icon(this LicenseState s) => s switch
    {
        LicenseState.Active => "i-check",
        LicenseState.ExpiringSoon => "i-clock",
        LicenseState.Expired => "i-alert",
        _ => "i-slash"
    };

    public static string Label(this LicenseFilter f) => f switch
    {
        LicenseFilter.Active => "正常",
        LicenseFilter.ExpiringSoon => "即将到期",
        LicenseFilter.Expired => "已过期",
        LicenseFilter.Revoked => "已吊销",
        LicenseFilter.Unactivated => "未激活",
        _ => "全部"
    };

    // 各档位在 SQL 侧对应的谓词。抽出来是为了让 OnGetAsync 的筛选分支和
    // 大屏的计数口径用的是同一套定义，不会出现"列表里算 3 张、大屏算 2 张"。
    public static System.Linq.Expressions.Expression<Func<LicenseKey, bool>> Predicate(
        this LicenseFilter f, DateTime utcNow)
    {
        var soon = utcNow.AddDays(ExpiringSoonDays);
        return f switch
        {
            LicenseFilter.Active => l => !l.IsRevoked && l.ExpiryUtc > soon,
            LicenseFilter.ExpiringSoon => l => !l.IsRevoked && l.ExpiryUtc > utcNow && l.ExpiryUtc <= soon,
            LicenseFilter.Expired => l => !l.IsRevoked && l.ExpiryUtc <= utcNow,
            LicenseFilter.Revoked => l => l.IsRevoked,
            LicenseFilter.Unactivated => l => !l.Activations.Any(a => !a.IsRevoked),
            _ => l => true
        };
    }

    // 把查询串里的字符串安全地转成枚举。任何无法识别的值一律回落 All，
    // 而不是抛异常——用户手改 URL 不该换来一个 500。
    public static LicenseFilter ParseFilter(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "active" => LicenseFilter.Active,
        "expiring" => LicenseFilter.ExpiringSoon,
        "expired" => LicenseFilter.Expired,
        "revoked" => LicenseFilter.Revoked,
        "unactivated" => LicenseFilter.Unactivated,
        _ => LicenseFilter.All
    };

    public static string Slug(this LicenseFilter f) => f switch
    {
        LicenseFilter.Active => "active",
        LicenseFilter.ExpiringSoon => "expiring",
        LicenseFilter.Expired => "expired",
        LicenseFilter.Revoked => "revoked",
        LicenseFilter.Unactivated => "unactivated",
        _ => "all"
    };
}
