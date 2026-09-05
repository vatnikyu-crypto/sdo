using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models;

namespace SdoApp.Controllers;

[Authorize]
public class DashboardController : Controller
{
    private readonly AppDbContext _context;

    // Внедряем контекст БД
    public DashboardController(AppDbContext context)
    {
        _context = context;
    }

    // ЛК Главного админа
    [Authorize(Roles = "Главный_Админ")]
    public IActionResult SuperAdmin()
    {
        return View();
    }

    // ЛК Обычного админа (Главная страница кабинета)
    [Authorize(Roles = "Админ")]
    public IActionResult Admin()
    {
        return View();
    }

    // НОВЫЙ МЕТОД: Список только Обучающихся для обычного Админа
    [Authorize(Roles = "Админ")]
    public async Task<IActionResult> AdminUsers()
    {
        // Фильтруем БД: берем только тех, у кого роль "Обучающийся"
        var students = await _context.Users
            .Include(u => u.Company)
            .Where(u => u.Role == UserRole.Обучающийся)
            .ToListAsync();

        return View(students);
    }

    // ЛК Слушателя (Обучающегося)
    [Authorize(Roles = "Обучающийся")]
    public IActionResult Student()
    {
        return View();
    }
}
