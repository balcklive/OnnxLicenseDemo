using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LicenseServer.Pages.Account;

public class LoginModel : PageModel
{
    private readonly IConfiguration _config;
    public LoginModel(IConfiguration config) => _config = config;

    [BindProperty] public string Password { get; set; } = "";
    public string? Error { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        // 密码不直接明文存在配置里，而是存它的 SHA256 哈希。
        // 生成方式见 DEPLOY.md：echo -n "你的密码" | sha256sum
        var expectedHash = _config["ADMIN_PASSWORD_SHA256"];
        if (string.IsNullOrEmpty(expectedHash))
        {
            Error = "服务器未配置管理员密码（环境变量 ADMIN_PASSWORD_SHA256），请联系运维";
            return Page();
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Password))).ToLowerInvariant();
        var expected = expectedHash.ToLowerInvariant();

        bool ok = actualHash.Length == expected.Length &&
                  CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actualHash), Encoding.UTF8.GetBytes(expected));

        if (!ok)
        {
            Error = "密码错误";
            return Page();
        }

        var claims = new List<Claim> { new(ClaimTypes.Name, "admin") };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        return RedirectToPage("/Admin/Index");
    }
}
