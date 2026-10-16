using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;

namespace Praja.Web.Services;

public static class WorkspaceIdentity
{
    public static Guid OwnerId(this ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public static async Task SignInAsync(HttpContext context, Operator user)
    {
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Name), new Claim(ClaimTypes.Email, user.Email), new Claim(ClaimTypes.Role, "Officer"), new Claim("Praja.CredentialStamp", CredentialStamp(user)) }, CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false });
    }
    public static PasswordHasher<Operator> Hasher { get; } = new();
    public static string CredentialStamp(Operator user) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user.PasswordHash + (user.AccountVersion == 0 ? "" : ":" + user.AccountVersion))));
    public static SemaphoreSlim SetupLock { get; } = new(1, 1);
}
