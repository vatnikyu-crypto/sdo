using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models;
using SdoApp.Models.ViewModels;
using SdoApp.Services;
using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using SdoApp.Helpers;
using System.Threading.Tasks;

namespace SdoApp.Controllers;

[Authorize(Roles = "Обучающийся")]
public class AssignmentsController : Controller
{
    private readonly AppDbContext _context;
    private readonly IWebHostEnvironment _env;
    private readonly AuditService _auditService;

    public AssignmentsController(AppDbContext context, IWebHostEnvironment env, AuditService auditService)
    {
        _context = context;
        _env = env;
        _auditService = auditService;
    }

    // 1. ОТКРЫТИЕ СТРАНИЦЫ СДАЧИ (GET) — с подгрузкой всех файлов
    [HttpGet]
    public async Task<IActionResult> SubmitPage(int materialId, int groupId)
    {
        var material = await _context.CourseMaterials
    .Include(m => m.Course)
    .Include(m => m.Files)   // <— ДОБАВИТЬ
    .FirstOrDefaultAsync(m => m.Id == materialId);
        if (material == null) return NotFound();

        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("UserId")?.Value;
        if (!int.TryParse(userIdClaim, out int studentId)) return Unauthorized();

        if (!await AccessService.HasAccessToMaterialAsync(_context, studentId, materialId, groupId))
            return this.ForbidSmart("Нет доступа к этому заданию.");

        string instruction = "";
        string fileUrl = "";

        if (!string.IsNullOrEmpty(material.ContentOrPath))
        {
            var parts = material.ContentOrPath.Split("|||");
            if (parts.Length > 1)
            {
                // Старый формат: url|||text
                fileUrl = parts[0];
                instruction = parts[1];
            }
            else
            {
                // Новый формат: только текст
                instruction = parts[0];
            }
        }

        if (instruction == "<p><br></p>" || string.IsNullOrWhiteSpace(instruction)) instruction = "";

        var submission = await _context.StudentSubmissions
    .Include(s => s.Messages).ThenInclude(m => m.Files)
    .Include(s => s.Messages).ThenInclude(m => m.Author) // <-- ДОБАВИЛИ ЭТО
    .FirstOrDefaultAsync(s => s.CourseMaterialId == materialId && s.StudentId == studentId && s.GroupId == groupId);

        var viewModel = new AssignmentSubmitViewModel
        {
            MaterialId = materialId,
            GroupId = groupId,
            CourseId = material.CourseId,
            Title = material.Title,
            CourseTitle = material.Course?.Title ?? "",
            Instruction = instruction,
            AttachmentUrl = string.IsNullOrWhiteSpace(fileUrl) ? null : fileUrl,

            // === ВОТ ЭТА СТРОКА ===
            TaskFiles = material.Files?.ToList() ?? new List<CourseMaterialFile>(),
            // =====================

            Status = submission?.Status ?? SubmissionStatus.OnReview,
            Grade = submission?.Grade,
            Messages = submission?.Messages.OrderBy(m => m.CreatedAt).ToList() ?? new List<SubmissionMessage>()
        };

        return View(viewModel);
    }

    // 2. ОТПРАВКА СООБЩЕНИЯ В ЧАТ (POST) — с сохранением всех файлов
    [HttpPost]
    public async Task<IActionResult> SendMessage(int materialId, int groupId, string? text, List<IFormFile>? files)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("UserId")?.Value;
        if (!int.TryParse(userIdClaim, out int studentId)) return Unauthorized();

        var material = await _context.CourseMaterials.FindAsync(materialId);
        if (material == null) return Json(new { success = false, message = "Материал не найден" });

        var submission = await _context.StudentSubmissions
            .FirstOrDefaultAsync(s => s.CourseMaterialId == materialId && s.StudentId == studentId && s.GroupId == groupId);

        if (submission != null && submission.Status == SubmissionStatus.Completed)
            return Json(new { success = false, message = "Задание уже проверено и закрыто. Писать в него нельзя." });

        bool hasFiles = files != null && files.Any();
        if (string.IsNullOrWhiteSpace(text) && !hasFiles)
            return Json(new { success = false, message = "Напишите текст или прикрепите файл." });

        if (hasFiles)
        {
            if (files!.Count > 3)
                return Json(new { success = false, message = "Можно прикрепить не более 3 файлов." });

            long totalSizeBytes = files.Sum(f => f.Length);
            if (totalSizeBytes > 75 * 1024 * 1024)
                return Json(new { success = false, message = "Общий размер файлов превышает 75 МБ." });

            foreach (var f in files)
            {
                if (!FileValidationHelper.IsAllowed(f, FileValidationHelper.SafeDocumentExtensions))
                    return Json(new { success = false, message = $"Файл «{f.FileName}» имеет недопустимый формат." });
            }
        }

        if (submission == null)
        {
            submission = new StudentSubmission
            {
                StudentId = studentId,
                CourseMaterialId = materialId,
                GroupId = groupId,
                CreatedAt = DateTime.UtcNow
            };
            _context.StudentSubmissions.Add(submission);
            await _context.SaveChangesAsync();
        }

        var message = new SubmissionMessage
        {
            StudentSubmissionId = submission.Id,
            AuthorId = studentId,
            TextContent = !string.IsNullOrWhiteSpace(text) ? text.Trim() : "Прикреплены файлы с решением",
            CreatedAt = DateTime.UtcNow
        };

        _context.SubmissionMessages.Add(message);
        await _context.SaveChangesAsync(); // Сохраняем, чтобы получить message.Id

        // === СОХРАНЯЕМ ВСЕ ФАЙЛЫ (до 3) ===
        if (hasFiles)
        {
            string uploadFolder = Path.Combine(_env.WebRootPath, "uploads", "submissions");
            if (!Directory.Exists(uploadFolder)) Directory.CreateDirectory(uploadFolder);

            foreach (var file in files!)
            {
                string extension = Path.GetExtension(file.FileName).ToLower();
                string uniqueFileName = Guid.NewGuid().ToString() + extension;
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

        submission.Status = SubmissionStatus.OnReview;
        submission.UpdatedAt = DateTime.UtcNow;
        submission.Grade = null;

        await _context.SaveChangesAsync();

        return Json(new { success = true });
    }

    // 3. ПОЛУЧЕНИЕ СТАТУСА (Для отображения истории на странице курса)
    [HttpGet]
    public async Task<IActionResult> GetState(int materialId, int groupId)
    {
        int studentId = User.GetUserId();
        if (studentId == 0) return Unauthorized();

        if (!await AccessService.HasAccessToMaterialAsync(_context, studentId, materialId, groupId))
            return this.ForbidSmart("Нет доступа к этому материалу.");

        var submission = await _context.StudentSubmissions
            .Include(s => s.Messages).ThenInclude(m => m.Files) // Подгружаем файлы
            .FirstOrDefaultAsync(s => s.CourseMaterialId == materialId && s.StudentId == studentId && s.GroupId == groupId);

        if (submission == null) return Json(new { hasSubmission = false, status = "NotStarted" });

        var chatHistory = submission.Messages
            .OrderBy(m => m.CreatedAt)
            .Select(m => new
            {
                isStudent = m.AuthorId == studentId,
                text = m.TextContent,
                file = m.FilePath, // Одиночный файл (старый формат)
                // Новый формат (список файлов)
                files = m.Files != null
                    ? m.Files.Select(f => (object)new { name = f.FileName, url = f.FilePath }).ToList()
                    : new List<object>(),
                date = m.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
            }).ToList();

        return Json(new { hasSubmission = true, status = submission.Status.ToString(), grade = submission.Grade, messages = chatHistory });
    }
}