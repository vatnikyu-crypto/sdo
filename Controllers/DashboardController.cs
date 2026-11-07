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

    public DashboardController(AppDbContext context)
    {
        _context = context;
    }

    [Authorize(Roles = "Главный_Админ")]
    public IActionResult SuperAdmin() 
    {
        // Перенаправляем на новый контроллер
        return RedirectToAction("Index", "SuperAdmin");
    }

    [Authorize(Roles = "Админ")]
    public IActionResult Admin() => View();

    [Authorize(Roles = "Админ")]
    public async Task<IActionResult> AdminUsers()
    {
        var students = await _context.Users
            .Include(u => u.Company)
            .Where(u => u.Role == UserRole.Обучающийся)
            .ToListAsync();
        return View(students);
    }

    [Authorize(Roles = "Обучающийся")]
    public async Task<IActionResult> Student()
    {
        var userIdClaim = User.FindFirst("UserId")?.Value;
        if (userIdClaim == null) return RedirectToAction("Login", "Auth");
        int studentId = int.Parse(userIdClaim);

        var student = await _context.Users
           .Include(u => u.Company)
           .FirstOrDefaultAsync(u => u.Id == studentId);

        ViewBag.CompanyLogoUrl = student?.Company?.LogoUrl;
        ViewBag.CompanyName = student?.Company?.Name ?? "Компания не указана";

        var myGroupIds = await _context.GroupStudents
            .Where(gs => gs.StudentId == studentId)
            .Select(gs => gs.GroupId)
            .ToListAsync();

        var today = DateTime.Today;

        var groupCourses = await _context.GroupCourseConfigs
            .Include(gcc => gcc.Course)
            .Include(gcc => gcc.Group)
            .Where(gcc => myGroupIds.Contains(gcc.GroupId))
            .ToListAsync();

        // Фильтр: курс виден, пока не истёк доступ к материалам (или дата окончания группы).
                // Фильтр: курс виден, пока не истёк доступ к материалам (или дата окончания группы)
        // + НОВОЕ: проверка, что студент реально допущен к курсу (AllowedStudentIds)
        var allCourses = groupCourses
            .Where(gcc =>
            {
                // 1. Проверка сроков
                var effectiveEnd = gcc.Group!.MaterialAccessDate ?? gcc.Group.EndDate;
                if (effectiveEnd.Date < today) return false;

                // 2. Проверка допуска
                // Если строка null, значит это курс, созданный до внедрения галочек (доступен всем)
                if (gcc.AllowedStudentIds == null) return true; 
                
                // Если строка пустая, значит галочки сняты со всех
                if (string.IsNullOrWhiteSpace(gcc.AllowedStudentIds)) return false;

                // Иначе проверяем, есть ли ID студента в списке
                var ids = gcc.AllowedStudentIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
                return ids.Contains(studentId);
            })
            .ToList();

        // Так как БД чистая, просто берем прогресс по ключу (GroupId + CourseId)
        var progressList = await _context.CourseProgresses
            .Where(p => p.StudentId == studentId)
            .ToListAsync();

        var progressByKey = progressList
            .Where(p => p.GroupId.HasValue)
            .GroupBy(p => $"{p.GroupId}_{p.CourseId}")
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(p => p.CompletedAt ?? DateTime.MinValue).First()
            );

        ViewBag.StudentProgress = progressByKey;

        var sortedCourses = allCourses
            .OrderBy(c =>
            {
                var key = $"{c.GroupId}_{c.CourseId}";
                return progressByKey.TryGetValue(key, out var p) && p.IsCompleted;
            })
            .ThenBy(c => c.Course?.Title)
            .ThenBy(c => c.Group?.GroupNumber)
            .ToList();

        return View(sortedCourses);
    }
}