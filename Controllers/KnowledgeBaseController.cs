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

[Authorize] // Доступ только для авторизованных пользователей СДО
public class KnowledgeBaseController : Controller
{
    private readonly AppDbContext _context;
    private readonly Microsoft.AspNetCore.Hosting.IWebHostEnvironment _env;

    public KnowledgeBaseController(AppDbContext context, Microsoft.AspNetCore.Hosting.IWebHostEnvironment env)
    {
        _context = context;
        _env = env;
    }

    // =================================================================
    // 1. ГЛАВНАЯ СТРАНИЦА (Доступна и Админу, и Студенту)
    // =================================================================
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        // Вытаскиваем все документы БЗ, свежие будут вверху списка
        var allDocs = await _context.KnowledgeBaseDocuments
            .OrderByDescending(d => d.UploadedAt)
            .ToListAsync();

        // Проверяем роль: если админ, то вернем админский View, если студент — студенческий
        if (User.IsInRole("Админ") || User.IsInRole("Главный_Админ"))
        {
            return View("AdminIndex", allDocs); // Админская панель управления
        }

        // =================================================================
        // ЛОГИКА ДЛЯ СТУДЕНТА: Проверка активного периода обучения
        // =================================================================

        // =================================================================
        // ЛОГИКА ДЛЯ СТУДЕНТА: Безопасное извлечение ID пользователя
        // =================================================================

        // Проверяем вообще ВСЕ возможные варианты, где может лежать ID
        var userIdStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                     ?? User.FindFirst("Id")?.Value
                     ?? User.FindFirst("UserId")?.Value;

        // Если в клеймах пусто, но ты сохранял ID в сам Identity.Name (так часто делают)
        if (string.IsNullOrEmpty(userIdStr))
        {
            userIdStr = User.Identity?.Name;
        }

        // Если вообще ничего не нашли, давай временно не будем выкидывать на Logout, 
        // а вернем ошибку в JSON, чтобы ты увидел её на экране вместо редиректа!
        if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out int studentId))
        {
            return Content($"<h3 style='color:red; font-family:sans-serif; text-align:center; margin-top:50px;'>" +
                           $"Ошибка СДО: Не удалось определить ID авторизованного пользователя!<br/>" +
                           $"Проверь, как записывается Id в AuthController.</h3>");
        }

        var today = DateTime.Today;

        // Достаем все конфигурации курсов для групп, в которых состоит этот студент
        var availableConfigs = await _context.GroupCourseConfigs
            .Where(cc => _context.GroupStudents.Any(gs => gs.GroupId == cc.GroupId && gs.StudentId == studentId))
            .ToListAsync();

        // Проверяем, есть ли хотя бы один курс, проходящий ПРЯМО СЕЙЧАС, к которому допущен этот студент
        bool hasActiveAccess = availableConfigs.Any(cc =>
        {
            // 1. Проверка индивидуальных дат курса (если они заданы)
            if (cc.CourseStartDate.HasValue && today < cc.CourseStartDate.Value.Date) return false;
            if (cc.CourseEndDate.HasValue && today > cc.CourseEndDate.Value.Date) return false;

            // 2. Проверка общих дат самой группы (если индивидуальные даты курса не заданы)
            if (!cc.CourseStartDate.HasValue && !cc.CourseEndDate.HasValue && cc.Group != null)
            {
                if (today < cc.Group.StartDate.Date || today > cc.Group.EndDate.Date) return false;
            }

            // 3. Проверка индивидуального допуска по ID
            // Если строка пустая — значит параметры допусков еще не настраивались точечно, 
            // и по умолчанию допущены ВСЕ студенты, зачисленные в эту группу.
            if (string.IsNullOrWhiteSpace(cc.AllowedStudentIds))
                return true;

            // Если строка заполнена, проверяем наличие ID конкретного студента
            var allowedIds = cc.AllowedStudentIds
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(id => int.TryParse(id.Trim(), out var parsedId) ? parsedId : -1)
                .ToList();

            return allowedIds.Contains(studentId);
        });


        // Если активного обучения нет — блокируем доступ и показываем View-заглушку
        if (!hasActiveAccess)
        {
            return View("AccessDenied");
        }

        // Если доступ есть — отдаем документы Базы Знаний
        var documents = await _context.KnowledgeBaseDocuments
            .OrderByDescending(d => d.UploadedAt)
            .ToListAsync();

        return View("StudentIndex", documents);
    }

    // =================================================================
    // 2. ЗАГРУЗКА НОВОГО ДОКУМЕНТА (Только для Администратора)
    // =================================================================
    [HttpPost]
    [Authorize(Roles = "Админ,Главный_Админ")]
    public async Task<IActionResult> Create(string documentNumber, string title, string? category, DateTime? revisionDate, IFormFile docFile)
    {
        if (docFile == null || docFile.Length == 0)
        {
            return Json(new { success = false, message = "Ошибка: Файл не выбран или пуст!" });
        }

        if (string.IsNullOrWhiteSpace(documentNumber) || string.IsNullOrWhiteSpace(title))
        {
            return Json(new { success = false, message = "Ошибка: Номер документа и Полное наименование обязательны!" });
        }

        string savedFilePath = string.Empty;
        string extension = Path.GetExtension(docFile.FileName).ToLower();

        // Считаем размер файла в Мегабайтах (округляем до 2 знаков)
        double sizeInMb = Math.Round((double)docFile.Length / (1024 * 1024), 2);

        try
        {
            // Путь к физической папке wwwroot/uploads/kb
            string kbFolder = Path.Combine(_env.WebRootPath, "uploads", "kb");
            if (!Directory.Exists(kbFolder))
            {
                Directory.CreateDirectory(kbFolder);
            }

            // Рандомное имя файла через Guid для защиты от дублирования имен
            string uniqueFileName = Guid.NewGuid().ToString() + extension;
            string fileSavePath = Path.Combine(kbFolder, uniqueFileName);

            using (var fileStream = new FileStream(fileSavePath, FileMode.Create))
            {
                await docFile.CopyToAsync(fileStream);
            }

            savedFilePath = $"/uploads/kb/{uniqueFileName}";
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Критический сбой диска при сохранении файла: {ex.Message}" });
        }

        // Подстраховка пустых полей
        string finalCategory = string.IsNullOrWhiteSpace(category) ? "Общие документы" : category.Trim();
        DateTime finalRevisionDate = revisionDate.HasValue ? revisionDate.Value.Date : DateTime.Today;

        var newDoc = new KnowledgeBaseDocument
        {
            DocumentNumber = documentNumber.Trim(),
            Title = title.Trim(),
            Category = finalCategory,
            FilePath = savedFilePath,
            FileExtension = extension,
            FileSizeMb = sizeInMb,
            DocumentRevisionDate = finalRevisionDate,
            UploadedAt = DateTime.UtcNow
        };

        try
        {
            _context.KnowledgeBaseDocuments.Add(newDoc);
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Ошибка записи реестра в БД: {ex.Message}" });
        }
    }

    // =================================================================
    // 2.1 РЕДАКТИРОВАНИЕ ДОКУМЕНТА (Только для Администратора)
    // =================================================================
    [HttpPost]
    [Authorize(Roles = "Админ,Главный_Админ")]
    public async Task<IActionResult> Edit(int id, string documentNumber, string title, string? category, DateTime? revisionDate, IFormFile? docFile)
    {
        if (string.IsNullOrWhiteSpace(documentNumber) || string.IsNullOrWhiteSpace(title))
        {
            return Json(new { success = false, message = "Ошибка: Номер документа и Полное наименование обязательны!" });
        }

        var doc = await _context.KnowledgeBaseDocuments.FindAsync(id);
        if (doc == null)
        {
            return Json(new { success = false, message = "Редактируемый документ не найден!" });
        }

        // Если админ прикрепил НОВЫЙ файл, заменяем старый
        if (docFile != null && docFile.Length > 0)
        {
            string extension = Path.GetExtension(docFile.FileName).ToLower();
            double sizeInMb = Math.Round((double)docFile.Length / (1024 * 1024), 2);

            try
            {
                // 1. Удаляем СТАРЫЙ физический файл с диска, чтобы не копить мусор
                string oldFullPath = Path.Combine(_env.WebRootPath, doc.FilePath.TrimStart('/'));
                if (System.IO.File.Exists(oldFullPath))
                {
                    System.IO.File.Delete(oldFullPath);
                }

                // 2. Сохраняем НОВЫЙ файл
                string kbFolder = Path.Combine(_env.WebRootPath, "uploads", "kb");
                string uniqueFileName = Guid.NewGuid().ToString() + extension;
                string fileSavePath = Path.Combine(kbFolder, uniqueFileName);

                using (var fileStream = new FileStream(fileSavePath, FileMode.Create))
                {
                    await docFile.CopyToAsync(fileStream);
                }

                // Обновляем файловые свойства в объекте
                doc.FilePath = $"/uploads/kb/{uniqueFileName}";
                doc.FileExtension = extension;
                doc.FileSizeMb = sizeInMb;
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"Ошибка при перезаписи файла: {ex.Message}" });
            }
        }

        // Обновляем текстовые поля
        doc.DocumentNumber = documentNumber.Trim();
        doc.Title = title.Trim();
        doc.Category = string.IsNullOrWhiteSpace(category) ? "Общие документы" : category.Trim();

        if (revisionDate.HasValue)
        {
            doc.DocumentRevisionDate = revisionDate.Value.Date;
        }

        try
        {
            _context.KnowledgeBaseDocuments.Update(doc);
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Ошибка обновления БД: {ex.Message}" });
        }
    }

    // =================================================================
    // 3. УДАЛЕНИЕ ДОКУМЕНТА И ФАЙЛА (Только для Администратора)
    // =================================================================
    [HttpPost]
    [Authorize(Roles = "Админ,Главный_Админ")]
    public async Task<IActionResult> Delete(int id)
    {
        var doc = await _context.KnowledgeBaseDocuments.FindAsync(id);
        if (doc == null)
        {
            return Json(new { success = false, message = "Документ не найден в системе!" });
        }

        try
        {
            // Удаляем физический файл с жесткого диска сервера, чтобы не забивать место
            string fullPath = Path.Combine(_env.WebRootPath, doc.FilePath.TrimStart('/'));
            if (System.IO.File.Exists(fullPath))
            {
                System.IO.File.Delete(fullPath);
            }

            // Удаляем запись из базы SQLite
            _context.KnowledgeBaseDocuments.Remove(doc);
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Ошибка при удалении: {ex.Message}" });
        }
    }
}
