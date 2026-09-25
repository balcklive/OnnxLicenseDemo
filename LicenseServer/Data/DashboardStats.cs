using Microsoft.EntityFrameworkCore;

namespace LicenseServer.Data;

// 大屏指标。
//
// 两条设计约束：
//
// 1. **全量口径。** 这些数字永远是"整个数据库"的统计，不接受列表的筛选条件。
//    大屏是仪表盘，不是列表的摘要——如果搜了某个产品就看不到总证数，那它就不是仪表盘。
//
// 2. **没有环比、没有趋势线。** 数据库里没有历史表，只有当前状态，存不下任何时间序列。
//    编一条没有数据支撑的 sparkline 比留白更糟，所以这里一个都不做。
public sealed record DashboardStats
{
    // —— 基础 ——
    public int LicenseTotal { get; init; }
    public int LicenseActivated { get; init; }    // 至少有 1 台有效设备的证
    public int LicenseUnactivated { get; init; }  // 一台有效设备都没有的证
    public int DeviceTotal { get; init; }         // 未撤销的设备总数

    // —— 到期预警 ——
    public int LicenseExpiring7d { get; init; }
    public int LicenseExpiring30d { get; init; }
    public int LicenseExpired { get; init; }

    // —— 活跃度 ——
    public int DeviceActive24h { get; init; }
    public int DeviceActive7d { get; init; }

    // —— 风险与容量 ——
    public int LicenseRevoked { get; init; }
    public int DeviceRevoked { get; init; }
    public int SeatsUsed { get; init; }   // 仅统计"有效许可证"下的席位
    public int SeatsTotal { get; init; }

    public DateTime ComputedUtc { get; init; }

    // 席位饱和度。SeatsTotal 为 0 时返回 0 而不是 NaN —— 空数据库不该让页面显示 "NaN%"。
    public double SeatSaturation => SeatsTotal == 0 ? 0d : (double)SeatsUsed / SeatsTotal;

    public int ActivationRate => LicenseTotal == 0
        ? 0
        : (int)Math.Round(100d * LicenseActivated / LicenseTotal);

    // 全部用轻量聚合查询算，不把整表拉进内存——旧代码是 _db.LicenseKeys.Include(...).ToListAsync()
    // 然后忽略掉聚合能力，导致全站数不出"一共有多少张证"。
    public static async Task<DashboardStats> LoadAsync(
        AppDbContext db, DateTime utcNow, CancellationToken ct = default)
    {
        var warnIn7 = utcNow.AddDays(LicenseStatus.CriticalExpiryDays);
        var warnIn30 = utcNow.AddDays(LicenseStatus.ExpiringSoonDays);
        var since24h = utcNow.AddHours(-24);
        var since7d = utcNow.AddDays(-7);

        // 有效许可证 = 未吊销 且 未过期。席位口径只算这批，
        // 否则被吊销/已过期的证会把"还能卖多少席位"这个数字撑得虚高。
        var validLicense = db.LicenseKeys.Where(l => !l.IsRevoked && l.ExpiryUtc > utcNow);

        var stats = new DashboardStats
        {
            LicenseTotal = await db.LicenseKeys.CountAsync(ct),

            LicenseRevoked = await db.LicenseKeys.CountAsync(l => l.IsRevoked, ct),
            LicenseExpired = await db.LicenseKeys.CountAsync(l => !l.IsRevoked && l.ExpiryUtc <= utcNow, ct),

            // 预警窗口用 [utcNow, 边界] 左闭右开，避免一张恰好卡在边界上的证
            // 同时被算进 7 天档和 30 天档。7 天档是 30 天档的子集，符合直觉。
            LicenseExpiring7d = await db.LicenseKeys.CountAsync(
                l => !l.IsRevoked && l.ExpiryUtc > utcNow && l.ExpiryUtc <= warnIn7, ct),
            LicenseExpiring30d = await db.LicenseKeys.CountAsync(
                l => !l.IsRevoked && l.ExpiryUtc > utcNow && l.ExpiryUtc <= warnIn30, ct),

            LicenseActivated = await db.LicenseKeys.CountAsync(
                l => l.Activations.Any(a => !a.IsRevoked), ct),
            LicenseUnactivated = await db.LicenseKeys.CountAsync(
                l => !l.Activations.Any(a => !a.IsRevoked), ct),

            DeviceTotal = await db.Activations.CountAsync(a => !a.IsRevoked, ct),
            DeviceRevoked = await db.Activations.CountAsync(a => a.IsRevoked, ct),
            DeviceActive24h = await db.Activations.CountAsync(
                a => !a.IsRevoked && a.LastHeartbeatUtc >= since24h, ct),
            DeviceActive7d = await db.Activations.CountAsync(
                a => !a.IsRevoked && a.LastHeartbeatUtc >= since7d, ct),

            SeatsUsed = await db.Activations.CountAsync(
                a => !a.IsRevoked && !a.LicenseKey.IsRevoked && a.LicenseKey.ExpiryUtc > utcNow, ct),
            SeatsTotal = await validLicense.SumAsync(l => (int?)l.MaxActivations, ct) ?? 0,

            ComputedUtc = utcNow
        };

        return stats;
    }
}
