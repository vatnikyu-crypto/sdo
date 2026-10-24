using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SdoApp.Data;

namespace SdoApp.Controllers;

[Authorize(Roles = "Обучающийся")]
public class ConsentController : Controller
{
    private readonly AppDbContext _context;

    public ConsentController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string? returnUrl)
    {
        var userIdClaim = User.FindFirst("UserId")?.Value;
        if (!int.TryParse(userIdClaim, out int userId))
            return RedirectToAction("Login", "Auth");

        var user = await _context.Users.FindAsync(userId);
        if (user == null)
            return RedirectToAction("Login", "Auth");

        // Если уже согласился — сразу в ЛК
        if (user.IsPrivacyAccepted)
            return RedirectToLocal(returnUrl);

        ViewBag.ReturnUrl = returnUrl;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(string? returnUrl, bool agreePolicy, bool agreeConsent)
    {
        if (!agreePolicy || !agreeConsent)
        {
            ViewBag.ReturnUrl = returnUrl;
            ViewBag.Error = "Для продолжения необходимо принять обе политики.";
            return View();
        }

        var userIdClaim = User.FindFirst("UserId")?.Value;
        if (!int.TryParse(userIdClaim, out int userId))
            return RedirectToAction("Login", "Auth");

        var user = await _context.Users.FindAsync(userId);
        if (user == null)
            return RedirectToAction("Login", "Auth");

        user.IsPrivacyAccepted = true;
        user.PrivacyAcceptedAt = DateTime.UtcNow;

        _context.Users.Update(user);
        await _context.SaveChangesAsync();

        return RedirectToLocal(returnUrl);
    }

    private IActionResult RedirectToLocal(string? returnUrl)
    {
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);

        return RedirectToAction("Student", "Dashboard");
    }

    [AllowAnonymous]
    [HttpGet]
    public IActionResult CookiePolicy()
    {
        return View();
    }
}