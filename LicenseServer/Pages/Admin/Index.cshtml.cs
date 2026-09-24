using System.Security.Cryptography;
using LicenseServer.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace LicenseServer.Pages.Admin;

[Authorize]
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    public List<LicenseKey> Licenses { get; set; } = new();
    public string? JustCreatedCode { get; set; }

    public async Task OnGetAsync()
    {
        Licenses = await _db.LicenseKeys.Include(l => l.Activations)
            .OrderByDescending(l => l.CreatedUtc).ToListAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync(string productName, int validDays, int maxActivations, string? modelKeyBase64)
    {
        var modelKey = string.IsNullOrWhiteSpace(modelKeyBase64)
            ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            : modelKeyBase64.Trim();

        var license = new LicenseKey
        {
            Code = GenerateCode(),
            ProductName = productName,
            ModelKeyBase64 = modelKey,
            MaxActivations = maxActivations,
            CreatedUtc = DateTime.UtcNow,
            ExpiryUtc = DateTime.UtcNow.AddDays(validDays)
        };
        _db.LicenseKeys.Add(license);
        await _db.SaveChangesAsync();

        JustCreatedCode = license.Code;
        Licenses = await _db.LicenseKeys.Include(l => l.Activations)
            .OrderByDescending(l => l.CreatedUtc).ToListAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRevokeLicenseAsync(int id)
    {
        var license = await _db.LicenseKeys.FindAsync(id);
        if (license is not null)
        {
            license.IsRevoked = true;
            await _db.SaveChangesAsync();
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokeActivationAsync(int id)
    {
        var activation = await _db.Activations.FindAsync(id);
        if (activation is not null)
        {
            activation.IsRevoked = true;
            await _db.SaveChangesAsync();
        }
        return RedirectToPage();
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
