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

    [HttpGet]
    public async Task<IActionResult> SubmitPage(int materialId, int groupId)
    {
        var material = await _context.CourseMaterials
            .Include(m => m.Course)
            .FirstOrDefaultAsync(m => m.Id == materialId);

        if (material == null) return NotFound();

        string instruction = "";
        string fileUrl = "";

        if (!string.IsNullOrEmpty(material.ContentOrPath))
        {
            var parts = material.ContentOrPath.Split("|||");
            fileUrl = parts.Length > 0 ? parts[0] : "";         // Ссылка на файл задания
            instruction = parts.Length > 1 ? parts[1] : "";    // HTML текст инструкции
        }

        // Если Quill сохранил пустоту как <p><br></p>, очищаем это
        if (instruction == "<p><br></p>" || string.IsNullOrWhiteSpace(instruction))
        {
            instruction = "";
        }

        var viewModel = new AssignmentSubmitViewModel
        {
            MaterialId = materialId,
            GroupId = groupId,
            CourseId = material.CourseId,
            Title = material.Title,
            CourseTitle = material.Course?.Title ?? "",
            Instruction = instruction,
            AttachmentUrl = string.IsNullOrWhiteSpace(fileUrl) ? null : fileUrl
        };

        return View(viewModel);
    }

    // 2. ОБРАБОТКА ФОРМЫ (Когда нажали "Отправить" на странице)
    [HttpPost]
    public async Task<IActionResult> SubmitPage([FromForm] AssignmentSubmitViewModel model)
    {
        int studentId = User.GetUserId();
        if (studentId == 0) return Unauthorized();

        if (!await AccessService.HasAccessToMaterialAsync(_context, studentId, model.MaterialId, model.GroupId))
            return this.ForbidSmart("Нет доступа к этому заданию.");

        if (string.IsNullOrWhiteSpace(model.StudentComment) && model.SubmissionFile == null)
        {
            TempData["ErrorMessage"] = "Напишите текст ответа или прикрепите файл!";
            return RedirectToAction("SubmitPage", new { materialId = model.MaterialId, groupId = model.GroupId });
        }

        // === НОВОЕ: Проверка на безопасное расширение ===
        if (model.SubmissionFile != null && model.SubmissionFile.Length > 0)
        {
            if (!FileValidationHelper.IsAllowed(model.SubmissionFile, FileValidationHelper.SafeDocumentExtensions))
            {
                TempData["ErrorMessage"] = "Недопустимый формат файла! Разрешены: документы, картинки, архивы.";
                return RedirectToAction("SubmitPage", new { materialId = model.MaterialId, groupId = model.GroupId });
            }
        }

        try
        {
            string savedFilePath = null;

            if (model.SubmissionFile != null && model.SubmissionFile.Length > 0)
            {
                string uploadsFolder = Path.Combine(_env.WebRootPath, "uploads", "submissions");
                if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

                string uniqueFileName = Guid.NewGuid().ToString() + Path.GetExtension(model.SubmissionFile.FileName);
                string fileSavePath = Path.Combine(uploadsFolder, uniqueFileName);

                using (var fileStream = new FileStream(fileSavePath, FileMode.Create))
                {
                    await model.SubmissionFile.CopyToAsync(fileStream);
                }
                savedFilePath = $"/uploads/submissions/{uniqueFileName}";
            }

            var submission = await _context.StudentSubmissions
                .FirstOrDefaultAsync(s => s.StudentId == studentId && s.CourseMaterialId == model.MaterialId && s.GroupId == model.GroupId);

            if (submission == null)
            {
                submission = new StudentSubmission
                {
                    StudentId = studentId,
                    CourseMaterialId = model.MaterialId,
                    GroupId = model.GroupId,
                    Status = SubmissionStatus.OnReview,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _context.StudentSubmissions.Add(submission);
            }
            else
            {
                submission.Status = SubmissionStatus.OnReview;
                submission.UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();

            var message = new SubmissionMessage
            {
                StudentSubmissionId = submission.Id,
                AuthorId = studentId,
                TextContent = string.IsNullOrWhiteSpace(model.StudentComment) ? "Прикреплен файл с решением" : model.StudentComment,
                FilePath = savedFilePath,
                CreatedAt = DateTime.UtcNow
            };
            _context.SubmissionMessages.Add(message);
            await _auditService.LogAsync("Create", "StudentSubmission", submission.Id, $"Студент отправил работу на проверку (Материал ID: {model.MaterialId})");
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = "Работа успешно отправлена на проверку!";
            return RedirectToAction("StudentView", "CourseMaterials", new { courseId = model.CourseId, groupId = model.GroupId, activeId = model.MaterialId });
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = $"Ошибка: {ex.Message}";
            return RedirectToAction("SubmitPage", new { materialId = model.MaterialId, groupId = model.GroupId });
        }
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
            .Include(s => s.Messages)
            .FirstOrDefaultAsync(s => s.CourseMaterialId == materialId && s.StudentId == studentId && s.GroupId == groupId);

        if (submission == null) return Json(new { hasSubmission = false, status = "NotStarted" });

        var chatHistory = submission.Messages
            .OrderBy(m => m.CreatedAt)
            .Select(m => new
            {
                isStudent = m.AuthorId == studentId,
                text = m.TextContent,
                file = m.FilePath,
                date = m.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
            }).ToList();

        return Json(new { hasSubmission = true, status = submission.Status.ToString(), grade = submission.Grade, messages = chatHistory });
    }
}