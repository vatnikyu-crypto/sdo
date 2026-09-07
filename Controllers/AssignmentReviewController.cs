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

[Authorize(Roles = "Админ")] // Доступ только для Администратора
public class AssignmentReviewController : Controller
{
    private readonly AppDbContext _context;

    public AssignmentReviewController(AppDbContext context)
    {
        _context = context;
    }

    // GET: /AssignmentReview/Index
    // Главная страница со списком всех присланных работ
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var submissions = await _context.StudentSubmissions
            .Include(s => s.CourseMaterial)
                .ThenInclude(m => m!.Course)
            .Include(s => s.Student)
            .OrderByDescending(s => s.SubmittedAt)
            .ToListAsync();

        return View(submissions);
    }

    // POST: /AssignmentReview/Review
    // Отправка рецензии (комментария) и выставление оценки за задание
    [HttpPost]
    public async Task<IActionResult> Review(int submissionId, string reviewText, int grade)
    {
        var submission = await _context.StudentSubmissions.FindAsync(submissionId);
        if (submission == null)
        {
            return Json(new { success = false, message = "Ответ студента не найден!" });
        }

        if (string.IsNullOrWhiteSpace(reviewText))
        {
            return Json(new { success = false, message = "Пожалуйста, напишите текст рецензии!" });
        }

        if (grade < 2 || grade > 5)
        {
            return Json(new { success = false, message = "Оценка должна быть в диапазоне от 2 до 5!" });
        }

        // Обновляем поля проверки (тот самый чат-ответ преподавателя)
        submission.AdminReviewText = reviewText.Trim();
        submission.Grade = grade;
        submission.ReviewedAt = DateTime.UtcNow;
        submission.IsReviewed = true; // Переводим задание в статус проверенного

        _context.StudentSubmissions.Update(submission);
        await _context.SaveChangesAsync();

        var material = await _context.CourseMaterials.FindAsync(submission.CourseMaterialId);
        if (material != null)
        {
            await GradeService.RecalculateCourseProgressAsync(_context, submission.StudentId, material.CourseId);
        }

        return Json(new { success = true });
    }

    // Вспомогательный API-метод для вызова счетчика в боковое меню через AJAX
    [HttpGet]
    public async Task<IActionResult> GetPendingCount()
    {
        int count = await _context.StudentSubmissions.CountAsync(s => !s.IsReviewed);
        return Json(new { count });
    }
}
