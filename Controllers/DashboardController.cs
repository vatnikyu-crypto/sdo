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
    public async Task<IActionResult> Student()
    {
        // 1. Вытаскиваем ID авторизованного студента из куки-сессии
        var userIdClaim = User.FindFirst("UserId")?.Value;
        if (userIdClaim == null) return RedirectToAction("Login", "Auth");
        int studentId = int.Parse(userIdClaim);

        // 2. Находим группы, в которых числится этот студент
        var myGroupIds = await _context.GroupStudents
            .Where(gs => gs.StudentId == studentId)
            .Select(gs => gs.GroupId)
            .ToListAsync();

        // 3. Вытаскиваем конфигурации курсов для этих групп
        var groupCourses = await _context.GroupCourseConfigs
            .Include(gcc => gcc.Course)
            .Where(gcc => myGroupIds.Contains(gcc.GroupId))
            .ToListAsync();

        // Трюк: убираем дубликаты курсов, если студент случайно оказался в двух группах с одним курсом
        var uniqueCourses = groupCourses
            .GroupBy(gc => gc.CourseId)
            .Select(g => g.First())
            .ToList();

        // 4. Загружаем все зачеты этого студента из журнала успеваемости
        var myProgresses = await _context.CourseProgresses
            .Where(p => p.StudentId == studentId)
            .ToDictionaryAsync(p => p.CourseId);

        // Передаем журнал успеваемости во ViewBag, чтобы во View сопоставить статус с карточкой курса
        ViewBag.StudentProgress = myProgresses;

        // Отправляем список привязанных конфигураций курсов во View
        return View(uniqueCourses);
    }
}
