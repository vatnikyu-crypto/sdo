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

        var today = DateTime.Today;

        // 3. Вытаскиваем конфигурации курсов с проверкой дат
        var groupCourses = await _context.GroupCourseConfigs
            .Include(gcc => gcc.Course)
            .Include(gcc => gcc.Group)
            .Where(gcc => myGroupIds.Contains(gcc.GroupId))
            .Where(gcc =>
                (gcc.Group!.MaterialAccessDate != null && gcc.Group.MaterialAccessDate.Value.Date >= today) ||
                (gcc.Group.MaterialAccessDate == null && gcc.Group.EndDate.Date >= today)
            )
            .ToListAsync();

        // Убираем дубликаты
        var uniqueCourses = groupCourses
            .GroupBy(gc => gc.CourseId)
            .Select(g => g.First())
            .ToList();

        // 4. Загружаем успеваемость
         var myProgresses = await _context.CourseProgresses
            .Where(p => p.StudentId == studentId)
            .GroupBy(p => p.CourseId)
            .Select(g => g.OrderByDescending(p => p.CompletedAt ?? DateTime.MinValue).First())
            .ToDictionaryAsync(p => p.CourseId);

        ViewBag.StudentProgress = myProgresses;

        // 5. СОРТИРОВКА ПРЯМО ЗДЕСЬ (В КОНТРОЛЛЕРЕ):
        // Сначала те, у которых IsCompleted == false (0), затем те, у которых true (1).
        // Внутри этих групп сортируем по алфавиту названия курса.
        var sortedCourses = uniqueCourses
            .OrderBy(c =>
            {
                bool isPassed = myProgresses.TryGetValue(c.CourseId, out var p) && p.IsCompleted;
                return isPassed; // false (0) встанет в начало, true (1) в конец
            })
            .ThenBy(c => c.Course?.Title)
            .ToList();

        // Отдаем в View УЖЕ отсортированный список
        return View(sortedCourses);
    }
}
