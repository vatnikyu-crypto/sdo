using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models;
using SdoApp.Services;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace SdoApp.Controllers;

[Authorize(Roles = "Админ,Главный_Админ")]
public class AssignmentReviewController : Controller
{
    private readonly AppDbContext _context;

    public AssignmentReviewController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        // Достаем тикеты вместе со всей цепочкой сообщений и их авторами
        var submissions = await _context.StudentSubmissions
            .Include(s => s.CourseMaterial).ThenInclude(m => m!.Course)
            .Include(s => s.Student)
            .Include(s => s.Messages).ThenInclude(m => m.Author)
            .Where(s => s.CourseMaterial != null && s.CourseMaterial.Type == MaterialType.Assignment)
            .OrderByDescending(s => s.UpdatedAt)
            .ToListAsync();

        return View(submissions);
    }

    [HttpPost]
    public async Task<IActionResult> Review(int submissionId, string reviewText, int? grade)
    {
        var submission = await _context.StudentSubmissions.FindAsync(submissionId);
        if (submission == null)
        {
            return Json(new { success = false, message = "Задание не найдено!" });
        }

        if (string.IsNullOrWhiteSpace(reviewText))
        {
            return Json(new { success = false, message = "Пожалуйста, напишите текст замечаний или рецензии!" });
        }

        if (grade.HasValue && (grade.Value < 2 || grade.Value > 5))
        {
            return Json(new { success = false, message = "Оценка должна быть в диапазоне от 2 до 5!" });
        }

        // Текущий проверяющий администратор (замени на реальный получение User.Id из сессии)
        int currentAdminId = 2; 

        // 1. Добавляем рецензию преподавателя как сообщение в чат
        var chatMessage = new SubmissionMessage
        {
            StudentSubmissionId = submission.Id,
            AuthorId = currentAdminId,
            TextContent = reviewText.Trim(),
            FilePath = null,
            CreatedAt = DateTime.UtcNow
        };
        _context.SubmissionMessages.Add(chatMessage);

        // 2. Меняем глобальный статус тикета
        if (grade.HasValue)
        {
            submission.Grade = grade;
            submission.Status = SubmissionStatus.Completed; // Работа принята, оценка стоит
        }
        else
        {
            submission.Grade = null;
            submission.Status = SubmissionStatus.NeedFix; // Отправлено на доработку
        }

        submission.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        // 3. Если проставили итоговую оценку, пересчитываем прогресс курса
        if (submission.Status == SubmissionStatus.Completed)
        {
            var material = await _context.CourseMaterials.FindAsync(submission.CourseMaterialId);
            if (material != null)
            {
                await GradeService.RecalculateCourseProgressAsync(_context, submission.StudentId, material.CourseId);
            }
        }

        return Json(new { success = true });
    }

    [HttpGet]
    public async Task<IActionResult> GetPendingCount()
    {
        // Считаем только те тикеты, которые висят в статусе "На проверке"
        int count = await _context.StudentSubmissions
            .CountAsync(s => s.Status == SubmissionStatus.OnReview && s.CourseMaterial != null && s.CourseMaterial.Type == MaterialType.Assignment);
        return Json(new { count });
    }
}
