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

    // Внедряем контекст БД через конструктор
    public AuthController(AppDbContext context)
    {
        _context = context;
    }

    // Отображение страницы входа: GET /Auth/Login
    [HttpGet]
    public IActionResult Login()
    {
        // Если пользователь уже авторизован, отправляем его в соответствующий ЛК
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToDashboard(User.FindFirst(ClaimTypes.Role)?.Value);
        }
        return View();
    }

    // Обработка данных формы авторизации: POST /Auth/Login
    [HttpPost]
    public async Task<IActionResult> Login([FromBody] LoginModel model)
    {
        if (model == null || string.IsNullOrWhiteSpace(model.Username) || string.IsNullOrWhiteSpace(model.Password))
        {
            return Json(new { success = false, message = "Заполните все поля!" });
        }

        // Ищем пользователя в БД по логину и паролю
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.Login == model.Username && u.Password == model.Password);

        if (user != null)
        {
            // Формируем список утверждений (Claims) для авторизации
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, user.Login),
                new Claim(ClaimTypes.Role, user.Role.ToString()), // Сохраняем роль
                new Claim("FullName", $"{user.FirstName} {user.LastName}"),
                new Claim("UserId", user.Id.ToString())
            };

            var claimsIdentity = new ClaimsIdentity(claims, "Cookies");

            // Инициализируем сессию (записываем шифрованный куки-файл в браузер)
            await HttpContext.SignInAsync("Cookies", new ClaimsPrincipal(claimsIdentity), new AuthenticationProperties
            {
                IsPersistent = true, // Запомнить браузер
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7) // Сессия живет 7 дней
            });

            // Определяем целевой URL на основе роли пользователя
            string redirectUrl = user.Role switch
            {
                UserRole.Главный_Админ => "/Dashboard/SuperAdmin",
                UserRole.Админ => "/Users/Index",
                _ => "/Dashboard/Student" // Для роли Обучающийся
            };

            return Json(new { success = true, redirectUrl = redirectUrl, message = "Успешный вход! Перенаправление..." });
        }

        return Json(new { success = false, message = "Неверный логин или пароль!" });
    }

    // Безопасный выход из системы: GET /Auth/Logout
    [HttpGet]
    public async Task<IActionResult> Logout()
    {
        // Уничтожаем сессионные куки
        await HttpContext.SignOutAsync("Cookies");
        return RedirectToAction("Login");
    }

    // Отображение страницы отказа в доступе (Ошибка 403): GET /Auth/AccessDenied
    [HttpGet]
    public IActionResult AccessDenied()
    {
        return View();
    }

    // Вспомогательный метод внутреннего редиректа по роли
    private IActionResult RedirectToDashboard(string? role)
    {
        return role switch
        {
            "Главный_Админ" => RedirectToAction("SuperAdmin", "Dashboard"),
            "Админ" => RedirectToAction("Admin", "Dashboard"),
            _ => RedirectToAction("Student", "Dashboard")
        };
    }
}

// Модель для десериализации JSON данных, приходящих от фронтенда
public class LoginModel
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
