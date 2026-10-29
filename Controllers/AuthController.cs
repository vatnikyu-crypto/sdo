using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models;
using System.Security.Claims;

namespace SdoApp.Controllers;

public class AuthController : Controller
{
    private readonly AppDbContext _context;

    public AuthController(AppDbContext context)
    {
        _context = context;
    }

    // GET: /Auth/Login?login=test7226&password=y4dRBKPFyn
    [HttpGet]
    public IActionResult Login(string? login, string? password)
    {
        // Если пользователь уже авторизован, отправляем его в ЛК
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToDashboard(User.FindFirst(ClaimTypes.Role)?.Value);
        }

        // Если пришли параметры из ссылки Excel — передаем их в HTML-форму
        if (!string.IsNullOrWhiteSpace(login) && !string.IsNullOrWhiteSpace(password))
        {
            ViewBag.AutoLogin = login;
            ViewBag.AutoPassword = password;
        }

        // Показываем страницу входа
        return View();
    }

    // POST: /Auth/Login (Для обычного входа через форму на сайте)
    [HttpPost]
    public async Task<IActionResult> Login([FromBody] LoginModel model)
    {
        if (model == null || string.IsNullOrWhiteSpace(model.Username) || string.IsNullOrWhiteSpace(model.Password))
        {
            return Json(new { success = false, message = "Заполните все поля!" });
        }

        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.Login == model.Username && u.Password == model.Password);

        if (user != null)
        {
            // Авторизуем пользователя
            await SignInUser(user);

            // Формируем URL для редиректа (как у тебя было в JS)
            string redirectUrl = user.Role switch
            {
                UserRole.Главный_Админ => "/Dashboard/SuperAdmin",
                UserRole.Админ => "/Dashboard/Admin",
                _ => "/Dashboard/Student"
            };

            return Json(new { success = true, redirectUrl = redirectUrl, message = "Успешный вход!" });
        }

        return Json(new { success = false, message = "Неверный логин или пароль!" });
    }

    // Вспомогательный метод для создания куки (чтобы не дублировать код)
    private async Task SignInUser(User user)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, user.Login),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
            new Claim("FullName", $"{user.FirstName} {user.LastName}"),
            new Claim("UserId", user.Id.ToString()),
            new Claim("Position", user.Position ?? "")
        };

        var claimsIdentity = new ClaimsIdentity(claims, "Cookies");

        await HttpContext.SignInAsync("Cookies", new ClaimsPrincipal(claimsIdentity), new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7)
        });
    }

    [HttpGet]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync("Cookies");
        return RedirectToAction("Login");
    }

    [HttpGet]
    public IActionResult AccessDenied()
    {
        return View();
    }

    // Твой вспомогательный метод (проверь, чтобы названия экшенов совпадали с твоими контроллерами)
    private IActionResult RedirectToDashboard(string? role)
    {
        return role switch
        {
            "Главный_Админ" => RedirectToAction("SuperAdmin", "Dashboard"),
            "Админ" => RedirectToAction("Admin", "Dashboard"), // Или на Users/Index, как в твоем коде
            _ => RedirectToAction("Student", "Dashboard")
        };
    }
}

public class LoginModel
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}