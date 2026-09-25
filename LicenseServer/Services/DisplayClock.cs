using System.Globalization;

namespace LicenseServer.Services;

// 数据库里的时间**一律是 UTC**，全网只在这里做一次时区转换，且只用于"显示"。
//
// 为什么需要它：管理员在中国（UTC+8），而界面过去把 UTC 原样打印出来再加一个 " UTC" 后缀。
// 一张北京时间 10-01 06:00 到期的证会显示成 "2026-09-30 22:00 UTC" —— 日期看着差一天。
// 对"7 天内到期"这种预警来说，这是会出事的。
//
// 关键约束：所有**判断**（是否过期、是否即将到期、心跳是否陈旧）都留在 UTC 里做，
// 只有走到视图输出那一刻才转换。把本地时间拿去和 UTC 列比较，是这类代码最经典的 bug ——
// 它会让每个预警窗口整体偏移 8 小时，而且不报错、只是悄悄算错。
public sealed class DisplayClock
{
    private readonly TimeZoneInfo _tz;

    public DisplayClock(IConfiguration config)
    {
        var id = config["DisplayTimeZone"];
        _tz = Resolve(id);
    }

    // 时区 id 配错不该让整个服务起不来（那会让一次手滑变成一次停机），
    // 识别不了就退回 UTC，行为等价于改造前。
    private static TimeZoneInfo Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    // 是否真的是 UTC —— 视图据此决定要不要显示时区标注，避免在 UTC 环境下
    // 显示一个等于没说的"北京时间"。
    public bool IsUtc => _tz.Id == TimeZoneInfo.Utc.Id;

    public string ZoneLabel => IsUtc ? "UTC" : _tz.Id;

    public DateTime ToLocal(DateTime utc)
    {
        // Npgsql 把 timestamptz 读回来时 Kind=Utc，这里 SpecifyKind 是防御性的：
        // 万一将来列类型变了导致 Kind=Unspecified，ConvertTimeFromUtc 会直接抛
        // ArgumentException。显式声明一次，把一个潜在崩溃点变成一个正确结果。
        var aware = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(aware, _tz);
    }

    public string Format(DateTime utc, string format = "yyyy-MM-dd HH:mm") =>
        ToLocal(utc).ToString(format, CultureInfo.InvariantCulture);

    public string FormatDate(DateTime utc) => Format(utc, "yyyy-MM-dd");

    // 给 <time datetime="..."> 用的机器可读值。
    public string Iso(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);

    // 悬停提示里给出原始 UTC，让"到底几点"永远可查证，不必信任转换。
    public string UtcTitle(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    // 心跳新鲜度。用相对时间而不是绝对时间，因为看这个列的目的是
    // "这台机器还活着吗"，绝对时刻反而需要心算。
    public string Relative(DateTime utc, DateTime utcNow)
    {
        var d = utcNow - utc;
        if (d < TimeSpan.Zero) return "刚刚";
        if (d < TimeSpan.FromMinutes(1)) return "刚刚";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} 分钟前";
        if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours} 小时前";
        if (d < TimeSpan.FromDays(30)) return $"{(int)d.TotalDays} 天前";
        return FormatDate(utc);
    }
}
