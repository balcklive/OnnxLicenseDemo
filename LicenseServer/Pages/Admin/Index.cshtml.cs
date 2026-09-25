using System.Security.Cryptography;
using LicenseServer.Data;
using LicenseServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace LicenseServer.Pages.Admin;

[Authorize]
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;

    public IndexModel(AppDbContext db, DisplayClock clock)
    {
        _db = db;
        Clock = clock;
    }

    public DisplayClock Clock { get; }

    // 每个请求只取一次"现在"，让大屏、筛选、徽章三处的判定基于同一个时刻。
    // 分散调用 DateTime.UtcNow 会让同一页上的数字出现毫秒级的互相矛盾。
    public DateTime Now { get; private set; }

    public DashboardStats Stats { get; private set; } = new();
    public List<LicenseKey> Licenses { get; private set; } = new();

    // 未经筛选的许可证总数。用来区分两种完全不同的"列表为空"：
    //   真的还没有任何许可证 vs 筛选条件把结果滤没了。
    // 这两种处境该给用户的下一步动作完全不同。
    public int TotalRows { get; private set; }

    // —— 筛选与排序状态 ——
    // SupportsGet：这些值来自查询串而不是表单体，所以 GET 请求也要绑定。
    // 状态放查询串（而不是服务端会话）意味着：链接可分享、刷新不丢、前进后退可用。
    [BindProperty(SupportsGet = true, Name = "q")] public string? Query { get; set; }
    [BindProperty(SupportsGet = true, Name = "status")] public string? Status { get; set; }
    [BindProperty(SupportsGet = true, Name = "sort")] public string? Sort { get; set; }

    public LicenseFilter Filter => LicenseStatus.ParseFilter(Status);

    public string SortSlug => string.IsNullOrWhiteSpace(Sort) ? "created" : Sort.Trim().ToLowerInvariant();

    public bool HasFilter => !string.IsNullOrWhiteSpace(Query) || Filter != LicenseFilter.All;

    // —— 有效期预设 ——
    // 每次建证都要手输天数是最没必要的一次重复劳动。这几档覆盖实际会卖出去的时长：
    // 短试用（3/7/15 天）+ 常见订阅周期（月/季/半年/年）。
    //
    // 这些是**快捷键而不是字段**：点了只是把值填进 ValidDays 输入框，
    // 提交上去的仍然只有 ValidDays 一个值。所以服务端不必知道有没有人用过预设，
    // 将来加档位也不用动 OnPostCreateAsync。
    private const int DefaultDays = 365;

    private static readonly DurationPreset[] PresetList = new DurationPreset[]
    {
        new("3 天", 3, "短期试用"),
        new("7 天", 7, "一周试用"),
        new("15 天", 15, null),
        new("1 个月", 30, "30 天"),
        new("3 个月", 90, "90 天"),
        new("6 个月", 180, "180 天"),
        new("1 年", DefaultDays, "365 天")
    };

    public IReadOnlyList<DurationPreset> DurationPresets => PresetList;

    // 输入框的默认值。与预设同源，避免两处写死后对不上。
    public int DefaultValidDays => DefaultDays;

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        Now = DateTime.UtcNow;

        // 大屏永远走全量口径，不吃筛选条件——它是指示仪表，不是列表的摘要。
        // 如果搜了个产品就看不到总证数，那它就不配叫仪表盘。
        Stats = await DashboardStats.LoadAsync(_db, Now);
        TotalRows = Stats.LicenseTotal;

        var query = _db.LicenseKeys.Include(l => l.Activations).AsSplitQuery();

        if (!string.IsNullOrWhiteSpace(Query))
        {
            // 转义 LIKE 元字符：用户输入的 % 和 _ 应该当普通字符匹配，
            // 否则搜一个 "%" 会命中全部记录。
            var raw = Query.Trim()
                .Replace("\\", "\\\\")
                .Replace("%", "\\%")
                .Replace("_", "\\_");
            var like = $"%{raw}%";

            // ILike：激活码是大写，但没人愿意为了搜索去开大写锁。
            // 同时搜机器指纹与机器名，这样"某台机器属于哪张证"也能直接搜到。
            query = query.Where(l =>
                EF.Functions.ILike(l.Code, like, "\\") ||
                EF.Functions.ILike(l.ProductName, like, "\\") ||
                l.Activations.Any(a =>
                    EF.Functions.ILike(a.MachineId, like, "\\") ||
                    (a.MachineName != null && EF.Functions.ILike(a.MachineName, like, "\\"))));
        }

        if (Filter != LicenseFilter.All)
        {
            query = query.Where(Filter.Predicate(Now));
        }

        query = SortSlug switch
        {
            // 到期升序 = 最紧急的排最前。这是这张表最常见的用途。
            "expiry" => query.OrderBy(l => l.ExpiryUtc).ThenByDescending(l => l.CreatedUtc),
            // 席位占用高的排前，方便发现"快满员、该提醒客户升级"的证。
            "seats" => query.OrderByDescending(l => l.Activations.Count(a => !a.IsRevoked))
                            .ThenByDescending(l => l.CreatedUtc),
            "name" => query.OrderBy(l => l.ProductName).ThenByDescending(l => l.CreatedUtc),
            _ => query.OrderByDescending(l => l.CreatedUtc)
        };

        Licenses = await query.ToListAsync();
    }

    // 创建许可证。
    //
    // 改为 POST-Redirect-GET：旧实现末尾是 return Page()，于是浏览器在结果页上按刷新
    // 会重发这个 POST —— **又生成一张重复的许可证**。这不是理论问题，是刷新一次就中一次。
    // 改成重定向后，刷新只是重新 GET 一次列表页。
    public async Task<IActionResult> OnPostCreateAsync(
        string productName, int validDays, int maxActivations, string? modelKeyBase64)
    {
        // 服务端校验。原来只靠 HTML5 的 required/min 属性，绕过表单直接 POST
        // 就能造出"已经过期"或"0 台设备"的许可证。
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(productName)) errors.Add("产品名称不能为空");
        if (validDays is < 1 or > 3650) errors.Add("有效天数需在 1～3650 之间");
        if (maxActivations is < 1 or > 10000) errors.Add("激活上限需在 1～10000 之间");

        if (errors.Count > 0)
        {
            TempData["CreateError"] = string.Join("；", errors);
            return RedirectToPage(new { q = Query, status = Status, sort = Sort });
        }

        // 留空 = 服务器现生成一把。这条路**保留但不再被推荐**：后台没有任何入口能把生成的密钥
        // 读回来，所以它产出的是一张"永远解不开自己模型"的证——客户端能激活、能进界面，
        // 只有模型加载会失败，而且服务端日志一切正常。页面上把这件事说清楚，见 Index.cshtml。
        var keyWasGenerated = string.IsNullOrWhiteSpace(modelKeyBase64);
        var modelKey = keyWasGenerated
            ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            : modelKeyBase64!.Trim();

        var now = DateTime.UtcNow;
        var license = new LicenseKey
        {
            Code = GenerateCode(),
            ProductName = productName.Trim(),
            ModelKeyBase64 = modelKey,
            MaxActivations = maxActivations,
            CreatedUtc = now,
            ExpiryUtc = now.AddDays(validDays)
        };
        _db.LicenseKeys.Add(license);
        await _db.SaveChangesAsync();

        // TempData 跨过一次重定向把激活码带到列表页。
        TempData["JustCreated"] = license.Code;
        // 指纹与"这把是不是服务器生成的"一起带过去：它是唯一能在建证那一刻就发现
        // "密钥跟包里的密文不是同一把"的信息（客户端 --license-check 也会打印同一个值）。
        TempData["JustCreatedKeyFingerprint"] = license.ModelKeyFingerprint();
        TempData["JustCreatedKeyWasGenerated"] = keyWasGenerated;
        return RedirectToPage(new { q = Query, status = Status, sort = Sort });
    }

    public async Task<IActionResult> OnPostRevokeLicenseAsync(int id)
    {
        var license = await _db.LicenseKeys.FindAsync(id);
        if (license is not null)
        {
            license.IsRevoked = true;
            await _db.SaveChangesAsync();
        }
        // 把筛选条件带回去。否则用户筛着"即将到期"撤销一张证，
        // 页面会跳回全部列表，他刚建立的工作上下文就没了。
        return RedirectToPage(new { q = Query, status = Status, sort = Sort });
    }

    public async Task<IActionResult> OnPostRevokeActivationAsync(int id)
    {
        var activation = await _db.Activations.FindAsync(id);
        if (activation is not null)
        {
            activation.IsRevoked = true;
            await _db.SaveChangesAsync();
        }
        return RedirectToPage(new { q = Query, status = Status, sort = Sort });
    }

    // 不含易混淆字符（0/O, 1/I），方便客户手打输入
    private static string GenerateCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<byte> buf = stackalloc byte[16];
        RandomNumberGenerator.Fill(buf);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 16; i++)
        {
            sb.Append(chars[buf[i] % chars.Length]);
            if (i % 4 == 3 && i != 15) sb.Append('-');
        }
        return sb.ToString();
    }
}

// 一档有效期预设。Days 是最终写进 ValidDays 的值，Title 是给鼠标悬停看的补充说明
// （比如"1 个月"其实按 30 天算，悬停能看到确切天数）。
public record DurationPreset(string Label, int Days, string? Title);
