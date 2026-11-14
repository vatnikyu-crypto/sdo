using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models;
using SdoApp.Services;
using SdoApp.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SdoApp.Controllers;

[Authorize(Roles = "Админ,Главный_Админ")]
public class AssignmentReviewController : Controller
{
    private readonly AppDbContext _context;
    private readonly IWebHostEnvironment _env;
    private readonly NotificationService _notificationService;

    public AssignmentReviewController(AppDbContext context, IWebHostEnvironment env, NotificationService notificationService)
    {
        _context = context;
        _env = env;
        _notificationService = notificationService;
    }

    // 1. ГЛАВНАЯ СТРАНИЦА (СПИСОК ЗАДАНИЙ)
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var submissions = await _context.StudentSubmissions
            .Include(s => s.CourseMaterial).ThenInclude(m => m!.Course)
            .Include(s => s.Student)
            .Include(s => s.Messages)
            .Where(s => s.CourseMaterial != null && s.CourseMaterial.Type == MaterialType.Assignment)
            .OrderBy(s => s.Status == SubmissionStatus.OnReview ? 1 : 2) // Сначала те, что ждут проверки
            .ThenByDescending(s => s.UpdatedAt)
            .ToListAsync();

        return View(submissions);
    }

    // 2. СЧЕТЧИК ДЛЯ МЕНЮ (ВОЗВРАЩАЕМ!)
    [HttpGet]
    public async Task<IActionResult> GetPendingCount()
    {
        int count = await _context.StudentSubmissions
            .CountAsync(s => s.Status == SubmissionStatus.OnReview && s.CourseMaterial != null && s.CourseMaterial.Type == MaterialType.Assignment);
        return Json(new { count });
    }

    // 3. ПОЛУЧЕНИЕ ДАННЫХ ДЛЯ ЧАТА (AJAX)
    [HttpGet]
    public async Task<IActionResult> GetDetails(int id)
    {
        var submission = await _context.StudentSubmissions
            .Include(s => s.Student)
            .Include(s => s.CourseMaterial).ThenInclude(m => m!.Course)
            .Include(s => s.Messages).ThenInclude(m => m.Author) // Подгружаем автора для ФИО
            .Include(s => s.Messages).ThenInclude(m => m.Files)
            .FirstOrDefaultAsync(s => s.Id == id);

        if (submission == null) return NotFound();

        var result = new
        {
            id = submission.Id,
            status = (int)submission.Status, // 0 - OnReview, 1 - NeedFix, 2 - Completed
            grade = submission.Grade,
            studentName = $"{submission.Student?.LastName} {submission.Student?.FirstName} {submission.Student?.MiddleName}".Trim(),
            studentId = submission.StudentId,
            courseTitle = submission.CourseMaterial?.Course?.Title,
            taskTitle = submission.CourseMaterial?.Title,
            messages = submission.Messages.OrderBy(m => m.CreatedAt).Select(m => new
            {
                id = m.Id,
                text = m.TextContent,
                // Если автор - студент, пишем "Студент". Если админ - "Преподаватель (ФИО)"
                senderName = m.AuthorId == submission.StudentId
                    ? "Студент"
                    : $"Преподаватель ({m.Author?.LastName} {m.Author?.FirstName} {m.Author?.MiddleName})".Trim(),
                isStudent = m.AuthorId == submission.StudentId,
                createdAt = m.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm"),
                files = m.Files.Select(f => new { name = f.FileName, url = f.FilePath }).ToList()
            }).ToList()
        };

        return Json(result);
    }

    // 4. ОТПРАВКА СООБЩЕНИЯ ОТ ПРЕПОДАВАТЕЛЯ
    [HttpPost]
    public async Task<IActionResult> SendMessage(int submissionId, string? text, List<IFormFile>? files)
    {
        var submission = await _context.StudentSubmissions
            .Include(s => s.CourseMaterial)
            .FirstOrDefaultAsync(s => s.Id == submissionId);

        if (submission == null) return Json(new { success = false, message = "Задание не найдено" });

        if (submission.Status == SubmissionStatus.Completed)
            return Json(new { success = false, message = "Задание закрыто. Общение недоступно." });

        bool hasFiles = files != null && files.Any();
        if (string.IsNullOrWhiteSpace(text) && !hasFiles)
            return Json(new { success = false, message = "Напишите текст или прикрепите файл." });

        if (hasFiles)
        {
            if (files!.Count > 3) return Json(new { success = false, message = "Максимум 3 файла." });
            if (files.Sum(f => f.Length) > 75 * 1024 * 1024) return Json(new { success = false, message = "Общий вес более 75 МБ." });
        }

        int currentAdminId = User.GetUserId();

        // Получаем ФИО админа для уведомления
        var admin = await _context.Users.FindAsync(currentAdminId);
        string adminName = admin != null ? $"{admin.LastName} {admin.FirstName} {admin.MiddleName}".Trim() : "Администратор";

        var message = new SubmissionMessage
        {
            StudentSubmissionId = submission.Id,
            AuthorId = currentAdminId,
            TextContent = !string.IsNullOrWhiteSpace(text) ? text.Trim() : "Прикреплен файл от преподавателя",
            CreatedAt = DateTime.UtcNow
        };

        _context.SubmissionMessages.Add(message);
        await _context.SaveChangesAsync();

        if (hasFiles)
        {
            string uploadFolder = Path.Combine(_env.WebRootPath, "uploads", "submissions");
            if (!Directory.Exists(uploadFolder)) Directory.CreateDirectory(uploadFolder);

            foreach (var file in files!)
            {
                string uniqueFileName = Guid.NewGuid().ToString() + Path.GetExtension(file.FileName);
                string savePath = Path.Combine(uploadFolder, uniqueFileName);

                using (var stream = new FileStream(savePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                _context.SubmissionFiles.Add(new SubmissionFile
                {
                    MessageId = message.Id,
                    FilePath = $"/uploads/submissions/{uniqueFileName}",
                    FileName = file.FileName
                });
            }
        }

        // Меняем статус на "На доработке"
        submission.Status = SubmissionStatus.NeedFix;
        submission.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        // Отправляем уведомление студенту (КРАСИВОЕ)
        await _notificationService.SendAsync(
            submission.StudentId,
            $"Преподаватель ({adminName}) ответил на ваше задание: «{submission.CourseMaterial?.Title}»",
            $"/Assignments/SubmitPage?materialId={submission.CourseMaterialId}&groupId={submission.GroupId}"
        );

        return Json(new { success = true });
    }

    // 5. ВЫСТАВЛЕНИЕ / ИЗМЕНЕНИЕ ОЦЕНКИ И ЗАКРЫТИЕ
    [HttpPost]
    public async Task<IActionResult> SetGrade(int submissionId, int grade, bool closeChat, bool reopenTask = false)
    {
        var submission = await _context.StudentSubmissions
            .Include(s => s.CourseMaterial)
            .FirstOrDefaultAsync(s => s.Id == submissionId);

        if (submission == null) return Json(new { success = false, message = "Задание не найдено" });

        int currentAdminId = User.GetUserId();
        var admin = await _context.Users.FindAsync(currentAdminId);
        string adminName = admin != null ? $"{admin.LastName} {admin.FirstName} {admin.MiddleName}".Trim() : "Администратор";

        if (reopenTask)
        {
            submission.Status = SubmissionStatus.NeedFix; // Открываем на доработку
            await _notificationService.SendAsync(
                submission.StudentId,
                $"Преподаватель ({adminName}) открыл задание заново для обсуждения: «{submission.CourseMaterial?.Title}»",
                $"/Assignments/SubmitPage?materialId={submission.CourseMaterialId}&groupId={submission.GroupId}"
            );
        }
        else
        {
            submission.Grade = grade;
            if (closeChat) submission.Status = SubmissionStatus.Completed;

            string notificationText = closeChat
                ? $"Преподаватель ({adminName}) выставил оценку {grade} и закрыл задание: «{submission.CourseMaterial?.Title}»"
                : $"Преподаватель ({adminName}) изменил оценку на {grade} в задании: «{submission.CourseMaterial?.Title}»";

            await _notificationService.SendAsync(
                submission.StudentId,
                notificationText,
                $"/Assignments/SubmitPage?materialId={submission.CourseMaterialId}&groupId={submission.GroupId}"
            );
        }

        submission.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        // === НОВОЕ: ПЕРЕСЧЕТ ПРОГРЕССА ПО КУРСУ ===
        // Если задание закрыто и оценка положительная (3, 4 или 5)
        if (submission.Status == SubmissionStatus.Completed && submission.Grade >= 3 && submission.CourseMaterial != null)
        {
            await GradeService.RecalculateCourseProgressAsync(
                _context,
                submission.StudentId,
                submission.CourseMaterial.CourseId,
                submission.GroupId
            );
        }
        // ==========================================

        return Json(new { success = true });
    }
}