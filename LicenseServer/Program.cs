using System.Security.Cryptography;
using LicenseServer.Data;
using LicenseServer.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ---- 数据库：PostgreSQL，连接串来自环境变量 ConnectionStrings__Default ----
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("请设置环境变量 ConnectionStrings__Default");
builder.Services.AddDbContext<AppDbContext>(opt => opt.UseNpgsql(connectionString));

builder.Services.AddSingleton<JwtService>();
builder.Services.AddRazorPages();

// ---- 管理后台登录用的 Cookie 认证（内置、成熟，不依赖第三方库）----
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// 首次启动自动建表。生产环境如果后续要改表结构，建议改用 EF Core Migrations，
// 这里为了"最小可运行"用 EnsureCreated 简化。
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

// =========================================================================
// 客户端调用的公开 API（不需要登录，客户端软件直接调用）
// =========================================================================

// 客户端启动时用来获取服务器公钥，本地校验令牌签名用
app.MapGet("/api/public-key", (JwtService jwt) => Results.Text(jwt.GetPublicKeyPem(), "text/plain"));

// 首次激活
app.MapPost("/api/activate", async (ActivateRequest req, AppDbContext db, JwtService jwt) =>
{
    var license = await db.LicenseKeys.Include(l => l.Activations)
        .FirstOrDefaultAsync(l => l.Code == req.Code);

    if (license is null) return Results.NotFound(new { error = "激活码不存在" });
    if (license.IsRevoked) return Results.Json(new { error = "激活码已被吊销" }, statusCode: 403);
    if (DateTime.UtcNow > license.ExpiryUtc) return Results.Json(new { error = "订阅已过期，请续费" }, statusCode: 403);

    var existing = license.Activations.FirstOrDefault(a => a.MachineId == req.MachineId);
    if (existing is not null)
    {
        if (existing.IsRevoked) return Results.Json(new { error = "此设备的激活已被管理员撤销" }, statusCode: 403);
        existing.LastHeartbeatUtc = DateTime.UtcNow;
        existing.MachineName = req.MachineName ?? existing.MachineName;
    }
    else
    {
        var activeCount = license.Activations.Count(a => !a.IsRevoked);
        if (activeCount >= license.MaxActivations)
            return Results.Json(new { error = $"激活设备数已达上限（{license.MaxActivations}台）" }, statusCode: 403);

        db.Activations.Add(new Activation
        {
            LicenseKeyId = license.Id,
            MachineId = req.MachineId,
            MachineName = req.MachineName,
            ActivatedUtc = DateTime.UtcNow,
            LastHeartbeatUtc = DateTime.UtcNow
        });
    }
    await db.SaveChangesAsync();

    var (token, expiresUtc) = jwt.IssueToken(req.MachineId, license.Code, license.ProductName, license.ModelKeyBase64, TimeSpan.FromDays(5));
    return Results.Ok(new TokenResponse(token, expiresUtc));
});

// 定期心跳：客户端每次启动 / 每隔一段时间调用，换取新令牌 + 让管理员能看到"最后在线时间"
app.MapPost("/api/heartbeat", async (ActivateRequest req, AppDbContext db, JwtService jwt) =>
{
    var license = await db.LicenseKeys.Include(l => l.Activations)
        .FirstOrDefaultAsync(l => l.Code == req.Code);

    if (license is null) return Results.NotFound(new { error = "激活码不存在" });
    if (license.IsRevoked) return Results.Json(new { error = "激活码已被吊销" }, statusCode: 403);
    if (DateTime.UtcNow > license.ExpiryUtc) return Results.Json(new { error = "订阅已过期，请续费" }, statusCode: 403);

    var activation = license.Activations.FirstOrDefault(a => a.MachineId == req.MachineId);
    if (activation is null) return Results.Json(new { error = "此设备尚未激活，请先激活" }, statusCode: 404);
    if (activation.IsRevoked) return Results.Json(new { error = "此设备的激活已被管理员撤销" }, statusCode: 403);

    activation.LastHeartbeatUtc = DateTime.UtcNow;
    await db.SaveChangesAsync();

    var (token, expiresUtc) = jwt.IssueToken(req.MachineId, license.Code, license.ProductName, license.ModelKeyBase64, TimeSpan.FromDays(5));
    return Results.Ok(new TokenResponse(token, expiresUtc));
});

app.Run();

public record ActivateRequest(string Code, string MachineId, string? MachineName);
public record TokenResponse(string Token, DateTime ExpiresUtc);
