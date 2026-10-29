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
    private readonly IWebHostEnvironment _env;

    public AssignmentReviewController(AppDbContext context, IWebHostEnvironment env)
    {
        _context = context;
        _env = env;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
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
    public async Task<IActionResult> Review(int submissionId, string reviewText, int? grade, IFormFile? adminFile) // <--- ДОБАВЛЕН adminFile
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

        int currentAdminId = 2; // Замени на реальный ID админа

        try
        {
            string savedFilePath = null;

            // СОХРАНЯЕМ ФАЙЛ АДМИНА, ЕСЛИ ОН ЕСТЬ
            if (adminFile != null && adminFile.Length > 0)
            {
                string uploadsFolder = Path.Combine(_env.WebRootPath, "uploads", "submissions", "admin_replies");
                if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

                string extension = Path.GetExtension(adminFile.FileName).ToLower();
                string uniqueFileName = Guid.NewGuid().ToString() + extension;
                string fileSavePath = Path.Combine(uploadsFolder, uniqueFileName);

                using (var fileStream = new FileStream(fileSavePath, FileMode.Create))
                {
                    await adminFile.CopyToAsync(fileStream);
                }
                savedFilePath = $"/uploads/submissions/admin_replies/{uniqueFileName}";
            }

            // 1. Добавляем рецензию преподавателя в чат
            var chatMessage = new SubmissionMessage
            {
                StudentSubmissionId = submission.Id,
                AuthorId = currentAdminId,
                TextContent = reviewText.Trim(),
                FilePath = savedFilePath, // <--- ПРИКРЕПЛЯЕМ ФАЙЛ К СООБЩЕНИЮ
                CreatedAt = DateTime.UtcNow
            };
            _context.SubmissionMessages.Add(chatMessage);

            // 2. Меняем глобальный статус
            if (grade.HasValue)
            {
                submission.Grade = grade;
                submission.Status = SubmissionStatus.Completed; 
            }
            else
            {
                submission.Grade = null;
                submission.Status = SubmissionStatus.NeedFix; 
            }

            submission.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            // 3. Пересчитываем прогресс, если задание закрыто
            if (submission.Status == SubmissionStatus.Completed)
            {
                var material = await _context.CourseMaterials.FindAsync(submission.CourseMaterialId);
                if (material != null)
                {
                    await GradeService.RecalculateCourseProgressAsync(_context, submission.StudentId, material.CourseId, submission.GroupId);
                }
            }

            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Ошибка сервера: {ex.Message}" });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetPendingCount()
    {
        int count = await _context.StudentSubmissions
            .CountAsync(s => s.Status == SubmissionStatus.OnReview && s.CourseMaterial != null && s.CourseMaterial.Type == MaterialType.Assignment);
        return Json(new { count });
    }
}