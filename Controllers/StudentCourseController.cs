using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models;
using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace SdoApp.Controllers;

[Authorize(Roles = "Обучающийся")] // Доступ открыт только для авторизованных студентов
public class StudentCourseController : Controller
{
    private readonly AppDbContext _context;
    private readonly IWebHostEnvironment _env;

    public StudentCourseController(AppDbContext context, IWebHostEnvironment env)
    {
        _context = context;
        _env = env;
    }

    // GET: /StudentCourse/GetAssignmentState?materialId=5
    // Используется фронтендом для динамической отрисовки чата и проверки статуса блокировок
    [HttpGet]
    public async Task<IActionResult> GetAssignmentState(int materialId)
    {
        // Извлекаем реальный ID текущего студента из его куки/токена авторизации
        int currentStudentId = GetCurrentUserId();
        if (currentStudentId == 0) return Challenge();

        // Ищем существующий тикет сдачи практической работы вместе со всеми сообщениями
        var submission = await _context.StudentSubmissions
            .Include(s => s.Messages)
            .FirstOrDefaultAsync(s => s.CourseMaterialId == materialId && s.StudentId == currentStudentId);

        // Если студент еще ни разу не отправлял ответ на это задание
        if (submission == null)
        {
            return Json(new { hasSubmission = false, status = "NotStarted" });
        }

        // Форматируем всю цепочку сообщений в легкий JSON для фронтенд-чата
        var chatHistory = submission.Messages
            .OrderBy(m => m.CreatedAt)
            .Select(m => new
            {
                isStudent = m.AuthorId == currentStudentId, // Помогает фронтенду понять, с какой стороны красить сообщение
                text = m.TextContent,
                file = m.FilePath,
                date = m.CreatedAt.ToString("dd.MM.yyyy HH:mm")
            }).ToList();

        // Возвращаем полный статус выполнения практической работы
        return Json(new
        {
            hasSubmission = true,
            status = submission.Status.ToString(), // Текстовые статусы: "OnReview", "NeedFix" или "Completed"
            grade = submission.Grade,
            messages = chatHistory
        });
    }

    // POST: /StudentCourse/SubmitAssignment
    // Принимает файлы и комментарии ответов (или исправлений после доработки) от студента
    [HttpPost]
    public async Task<IActionResult> SubmitAssignment(int materialId, string? studentComment, IFormFile? submissionFile)
    {
        // Извлекаем реальный ID текущего студента из контекста авторизации
        int currentStudentId = GetCurrentUserId();
        if (currentStudentId == 0) return Challenge();

        // Ищем текущий тикет сдачи в базе данных
        var submission = await _context.StudentSubmissions
            .FirstOrDefaultAsync(s => s.CourseMaterialId == materialId && s.StudentId == currentStudentId);

        // ЗАЩИТА ОТ СПАМА: Если тикет уже на проверке у админа или завершен — блокируем повторную отправку
        if (submission != null && submission.Status != SubmissionStatus.NeedFix)
        {
            return Json(new { success = false, message = "Задание уже отправлено на проверку или зафиксировано итоговой оценкой!" });
        }

        string? relativePath = null;

        // Если студент прикрепил файл — сохраняем его на физический диск сервера
        if (submissionFile != null && submissionFile.Length > 0)
        {
            var uploadsFolder = Path.Combine(_env.WebRootPath, "uploads", "submissions");
            if (!Directory.Exists(uploadsFolder))
            {
                Directory.CreateDirectory(uploadsFolder);
            }

            // Генерируем уникальное имя файла с GUID, чтобы файлы студентов не затирали друг друга
            var uniqueFileName = $"{Guid.NewGuid()}_{Path.GetFileName(submissionFile.FileName)}";
            var filePath = Path.Combine(uploadsFolder, uniqueFileName);

            using (var fileStream = new FileStream(filePath, FileMode.Create))
            {
                await submissionFile.CopyToAsync(fileStream);
            }

            relativePath = $"/uploads/submissions/{uniqueFileName}";
        }
        else if (submission == null)
        {
            // При самой первой попытке сдачи файла — прикрепление решения строго обязательно
            return Json(new { success = false, message = "Для первой сдачи практического задания необходимо прикрепить файл с решением!" });
        }

        // ШАГ 1: Управляем заголовком (тикетом) сдачи практической работы
        if (submission == null)
        {
            // Самая первая сдача материала студентом — создаем новую шапку тикета
            submission = new StudentSubmission
            {
                CourseMaterialId = materialId,
                StudentId = currentStudentId,
                Status = SubmissionStatus.OnReview,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _context.StudentSubmissions.Add(submission);
            await _context.SaveChangesAsync(); // Сохраняем, чтобы сгенерировать submission.Id
        }
        else
        {
            // Это доработка после замечаний преподавателя — переводим тикет обратно в статус "На проверке"
            submission.Status = SubmissionStatus.OnReview;
            submission.UpdatedAt = DateTime.UtcNow;
        }

        // ШАГ 2: Добавляем новое текстовое/файловое сообщение в общую ленту чата этого тикета
        var message = new SubmissionMessage
        {
            StudentSubmissionId = submission.Id,
            AuthorId = currentStudentId,
            TextContent = studentComment?.Trim() ?? "Направлена доработанная версия решения.",
            FilePath = relativePath, // Будет null, если студент отправляет только текстовое сообщение в чат
            CreatedAt = DateTime.UtcNow
        };

        _context.SubmissionMessages.Add(message);
        await _context.SaveChangesAsync();

        return Json(new { success = true });
    }

    // Вспомогательный приватный метод для безопасного извлечения ID из твоей системы авторизации
    private int GetCurrentUserId()
    {
        // Ищем клейм с именем "UserId", которое ты заложил в AuthController
        var userIdClaim = User.FindFirst("UserId")?.Value;

        if (int.TryParse(userIdClaim, out int userId))
        {
            return userId;
        }
        return 0; // Возвращает 0, если пользователь не распознан системой
    }
}
