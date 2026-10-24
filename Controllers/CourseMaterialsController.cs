using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SdoApp.Controllers;

[Authorize]
public class CourseMaterialsController : Controller
{
    private readonly AppDbContext _context;
    private readonly IWebHostEnvironment _env;

    public CourseMaterialsController(AppDbContext context, IWebHostEnvironment env)
    {
        _context = context;
        _env = env;
    }

    // GET: /CourseMaterials/Index?courseId=5
    [HttpGet]
    public async Task<IActionResult> Index(int courseId)
    {
        // ВАЖНО: Загружаем темы, а внутрь тем - их материалы
        var course = await _context.Courses
            .Include(c => c.Themes.OrderBy(t => t.Order))
                .ThenInclude(t => t.Materials.OrderBy(m => m.Order))
                .Include(c => c.Materials)
            .FirstOrDefaultAsync(c => c.Id == courseId);

        if (course == null) return NotFound();

        ViewBag.CourseId = course.Id;
        ViewBag.CourseTitle = course.Title;

        return View(course); // Теперь передаем весь курс, а не просто список материалов
    }

    [HttpPost]
    [Authorize(Roles = "Админ")]
    public async Task<IActionResult> CreateTheme(int courseId, string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Json(new { success = false, message = "Название темы обязательно!" });

        int nextOrder = 1;
        var existingThemes = await _context.CourseThemes.Where(t => t.CourseId == courseId).ToListAsync();
        if (existingThemes.Any()) nextOrder = existingThemes.Max(t => t.Order) + 1;

        var theme = new CourseTheme { CourseId = courseId, Title = title.Trim(), Order = nextOrder };
        _context.CourseThemes.Add(theme);
        await _context.SaveChangesAsync();
        return Json(new { success = true });
    }

    [HttpPost]
    [Authorize(Roles = "Админ")]
    public async Task<IActionResult> DeleteTheme(int id)
    {
        var theme = await _context.CourseThemes.Include(t => t.Materials).FirstOrDefaultAsync(t => t.Id == id);
        if (theme == null) return Json(new { success = false, message = "Тема не найдена!" });

        // Отвязываем материалы от удаляемой темы (чтобы они не удалились вместе с ней, а попали в "Без темы")
        foreach (var m in theme.Materials) { m.ThemeId = null; }

        _context.CourseThemes.Remove(theme);
        await _context.SaveChangesAsync();
        return Json(new { success = true });
    }

    [HttpPost]
    [Authorize(Roles = "Админ")]
    public async Task<IActionResult> Create(int courseId, int? themeId, string title, MaterialType type, string? textContent, IFormFile? uploadedFile, int? timeLimit, bool shuffleQuestions, int? passPercentage, TestStatus testKind, int? questionsCountToUse)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Json(new { success = false, message = "Название элемента обязательно!" });

        string? contentOrPath = null;

        if (type == MaterialType.Text) { contentOrPath = textContent ?? ""; }
        else if (type == MaterialType.Pdf || type == MaterialType.Video || type == MaterialType.Image)
        {
            if (uploadedFile == null || uploadedFile.Length == 0) return Json(new { success = false, message = "Прикрепите файл!" });
            var extension = Path.GetExtension(uploadedFile.FileName).ToLower();
            if (type == MaterialType.Pdf && extension != ".pdf") return Json(new { success = false, message = "Только .pdf!" });
            if (type == MaterialType.Video && extension != ".mp4") return Json(new { success = false, message = "Только .mp4!" });
            if (type == MaterialType.Image && !new[] { ".jpg", ".jpeg", ".png", ".webp" }.Contains(extension)) return Json(new { success = false, message = "Только .jpg, .png, .webp!" });

            string subFolder = type == MaterialType.Pdf ? "pdfs" : type == MaterialType.Video ? "videos" : "images";
            string uploadsFolder = Path.Combine(_env.WebRootPath, "uploads", "materials", subFolder);
            if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

            string uniqueFileName = Guid.NewGuid().ToString() + extension;
            using (var fileStream = new FileStream(Path.Combine(uploadsFolder, uniqueFileName), FileMode.Create)) { await uploadedFile.CopyToAsync(fileStream); }
            contentOrPath = $"/uploads/materials/{subFolder}/{uniqueFileName}";
        }
        else if (type == MaterialType.Assignment)
        {
            string fileUrl = "";
            if (uploadedFile != null && uploadedFile.Length > 0)
            {
                string uploadsFolder = Path.Combine(_env.WebRootPath, "uploads", "materials", "assignments");
                if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);
                string uniqueFileName = Guid.NewGuid().ToString() + Path.GetExtension(uploadedFile.FileName);
                using (var fileStream = new FileStream(Path.Combine(uploadsFolder, uniqueFileName), FileMode.Create)) { await uploadedFile.CopyToAsync(fileStream); }
                fileUrl = $"/uploads/materials/assignments/{uniqueFileName}";
            }
            contentOrPath = $"{fileUrl}|||{textContent ?? ""}";
        }

        // Вычисляем Order внутри выбранной темы (или среди материалов без темы)
        int nextOrder = 1;
        var existingMaterials = await _context.CourseMaterials.Where(m => m.CourseId == courseId && m.ThemeId == themeId).ToListAsync();
        if (existingMaterials.Any()) { nextOrder = existingMaterials.Max(m => m.Order) + 1; }

        var newMaterial = new CourseMaterial
        {
            CourseId = courseId,
            ThemeId = themeId, // <--- ПРИВЯЗКА К ТЕМЕ
            Title = title.Trim(),
            Type = type,
            ContentOrPath = contentOrPath,
            Order = nextOrder,
            TimeLimit = type == MaterialType.Test ? (timeLimit ?? 0) : 0,
            ShuffleQuestions = type == MaterialType.Test && shuffleQuestions,
            PassPercentage = type == MaterialType.Test ? (passPercentage ?? 80) : 80,
            TestKind = type == MaterialType.Test ? testKind : TestStatus.Промежуточный,
            QuestionsCountToUse = type == MaterialType.Test ? (questionsCountToUse ?? 0) : 0
        };

        _context.CourseMaterials.Add(newMaterial);
        await _context.SaveChangesAsync();
        return Json(new { success = true });
    }


    // POST: /CourseMaterials/ChangeOrder
    // Смена порядка элементов (сортировка стрелочками вверх/вниз)
    [HttpPost]
    [Authorize(Roles = "Админ")]
    public async Task<IActionResult> ChangeOrder(int id, string direction)
    {
        var currentMaterial = await _context.CourseMaterials.FindAsync(id);
        if (currentMaterial == null)
        {
            return Json(new { success = false, message = "Элемент не найден!" });
        }

        // Ищем все элементы этого же курса, отсортированные по порядку
        var materials = await _context.CourseMaterials
            .Where(m => m.CourseId == currentMaterial.CourseId)
            .OrderBy(m => m.Order)
            .ToListAsync();

        int currentIndex = materials.FindIndex(m => m.Id == id);
        if (currentIndex == -1) return Json(new { success = false });

        CourseMaterial? swapMaterial = null;

        if (direction == "up" && currentIndex > 0)
        {
            // Ищем верхнего соседа для обмена местами
            swapMaterial = materials[currentIndex - 1];
        }
        else if (direction == "down" && currentIndex < materials.Count - 1)
        {
            // Ищем нижнего соседа для обмена местами
            swapMaterial = materials[currentIndex + 1];
        }

        if (swapMaterial != null)
        {
            // Меняем значения Order местами
            int tempOrder = currentMaterial.Order;
            currentMaterial.Order = swapMaterial.Order;
            swapMaterial.Order = tempOrder;

            _context.CourseMaterials.Update(currentMaterial);
            _context.CourseMaterials.Update(swapMaterial);
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }

        return Json(new { success = false, message = "Перемещение невозможно!" });
    }

    // POST: /CourseMaterials/Delete/5
    // Асинхронное удаление элемента курса с пересчетом порядка Order
    [HttpPost]
    [Authorize(Roles = "Админ")]
    public async Task<IActionResult> Delete(int id)
    {
        var material = await _context.CourseMaterials.FindAsync(id);
        if (material == null)
        {
            return Json(new { success = false, message = "Элемент курса не найден!" });
        }

        int courseId = material.CourseId;
        int deletedOrder = material.Order;

        try
        {
            // Если это был файл (PDF, Видео, Картинка или Файл задания) — физически удаляем его с диска сервера
            if (!string.IsNullOrEmpty(material.ContentOrPath) && material.ContentOrPath.StartsWith("/uploads/"))
            {
                // Для заданий отсекаем путь к файлу перед разделителем |||
                string filePath = material.Type == MaterialType.Assignment
                    ? material.ContentOrPath.Split("|||")[0]
                    : material.ContentOrPath;

                if (!string.IsNullOrEmpty(filePath) && filePath.StartsWith("/uploads/"))
                {
                    string absolutePath = Path.Combine(_env.WebRootPath, filePath.TrimStart('/'));
                    if (System.IO.File.Exists(absolutePath)) System.IO.File.Delete(absolutePath);
                }
            }

            _context.CourseMaterials.Remove(material);
            await _context.SaveChangesAsync();

            // Магия пересчета: сдвигаем порядок всех последующих элементов курса назад на 1
            var remainingMaterials = await _context.CourseMaterials
                .Where(m => m.CourseId == courseId && m.Order > deletedOrder)
                .ToListAsync();

            foreach (var rm in remainingMaterials)
            {
                rm.Order--;
                _context.CourseMaterials.Update(rm);
            }
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Ошибка при удалении: {ex.Message}" });
        }
    }

    // GET: /CourseMaterials/GetMaterialData/5
    [HttpGet]
    public async Task<IActionResult> GetMaterialData(int id)
    {
        var m = await _context.CourseMaterials.FindAsync(id);
        if (m == null) return NotFound();

        string textContent = "";
        string fileUrl = "";

        // Обрабатываем типы в соответствии с базой данных
        if (m.Type == MaterialType.Text)
        {
            textContent = m.ContentOrPath ?? "";
        }
        else if (m.Type == MaterialType.Assignment)
        {
            var parts = m.ContentOrPath?.Split("|||") ?? new string[] { "", "" };
            fileUrl = parts.Length > 0 ? parts[0] : "";
            textContent = parts.Length > 1 ? parts[1] : "";
        }
        // ВАЖНОЕ ИСПРАВЛЕНИЕ: Передаем путь к файлу для медиа-материалов!
        else if (m.Type == MaterialType.Pdf || m.Type == MaterialType.Video || m.Type == MaterialType.Image)
        {
            fileUrl = m.ContentOrPath ?? "";
        }

        return Json(new
        {
            id = m.Id,
            title = m.Title,
            type = (int)m.Type,
            textContent = textContent,
            fileUrl = fileUrl,
            timeLimit = m.TimeLimit,
            shuffleQuestions = m.ShuffleQuestions,
            passPercentage = m.PassPercentage,
            testKind = (int)m.TestKind,
            questionsCountToUse = m.QuestionsCountToUse
        });
    }


    // POST: /CourseMaterials/Edit
    [HttpPost]
    [Authorize(Roles = "Админ")]
    public async Task<IActionResult> Edit(int id, int? themeId, string title, string? textContent, IFormFile? uploadedFile, int? timeLimit, bool shuffleQuestions, int? passPercentage, TestStatus testKind, int? questionsCountToUse)
    {
        var material = await _context.CourseMaterials.FindAsync(id);
        if (material == null) return Json(new { success = false, message = "Элемент не найден!" });
        if (string.IsNullOrWhiteSpace(title)) return Json(new { success = false, message = "Название обязательно!" });

        material.Title = title.Trim();
        material.ThemeId = themeId; // <--- Обновляем привязку

        // ... (остальная логика обработки файлов и тестов остается как была) ...
        if (material.Type == MaterialType.Text) { material.ContentOrPath = textContent ?? ""; }
        else if (material.Type == MaterialType.Pdf || material.Type == MaterialType.Video || material.Type == MaterialType.Image)
        {
            if (uploadedFile != null && uploadedFile.Length > 0)
            {
                // Логика замены файла...
                var extension = Path.GetExtension(uploadedFile.FileName).ToLower();
                if (material.Type == MaterialType.Pdf && extension != ".pdf") return Json(new { success = false, message = "Только .pdf!" });
                if (material.Type == MaterialType.Video && extension != ".mp4") return Json(new { success = false, message = "Только .mp4!" });
                if (material.Type == MaterialType.Image && !new[] { ".jpg", ".jpeg", ".png", ".webp" }.Contains(extension)) return Json(new { success = false, message = "Только картинки!" });

                if (!string.IsNullOrEmpty(material.ContentOrPath) && material.ContentOrPath.StartsWith("/uploads/"))
                {
                    string oldAbsolute = Path.Combine(_env.WebRootPath, material.ContentOrPath.TrimStart('/'));
                    if (System.IO.File.Exists(oldAbsolute)) System.IO.File.Delete(oldAbsolute);
                }
                string subFolder = material.Type == MaterialType.Pdf ? "pdfs" : material.Type == MaterialType.Video ? "videos" : "images";
                string uploadsFolder = Path.Combine(_env.WebRootPath, "uploads", "materials", subFolder);
                if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);
                string uniqueFileName = Guid.NewGuid().ToString() + extension;
                using (var fileStream = new FileStream(Path.Combine(uploadsFolder, uniqueFileName), FileMode.Create)) { await uploadedFile.CopyToAsync(fileStream); }
                material.ContentOrPath = $"/uploads/materials/{subFolder}/{uniqueFileName}";
            }
        }
        else if (material.Type == MaterialType.Assignment)
        {
            if (uploadedFile != null && uploadedFile.Length > 0)
            {
                // Логика замены файла задания...
                string uploadsFolder = Path.Combine(_env.WebRootPath, "uploads", "materials", "assignments");
                if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);
                string uniqueFileName = Guid.NewGuid().ToString() + Path.GetExtension(uploadedFile.FileName);
                using (var fileStream = new FileStream(Path.Combine(uploadsFolder, uniqueFileName), FileMode.Create)) { await uploadedFile.CopyToAsync(fileStream); }
                string oldFile = material.ContentOrPath?.Split("|||")[0] ?? "";
                if (!string.IsNullOrEmpty(oldFile) && oldFile.StartsWith("/uploads/"))
                {
                    string oldAbsolute = Path.Combine(_env.WebRootPath, oldFile.TrimStart('/'));
                    if (System.IO.File.Exists(oldAbsolute)) System.IO.File.Delete(oldAbsolute);
                }
                material.ContentOrPath = $"/uploads/materials/assignments/{uniqueFileName}|||{textContent ?? ""}";
            }
            else
            {
                string currentFile = material.ContentOrPath?.Split("|||")[0] ?? "";
                material.ContentOrPath = $"{currentFile}|||{textContent ?? ""}";
            }
        }
        else if (material.Type == MaterialType.Test)
        {
            material.TimeLimit = timeLimit ?? 0;
            material.ShuffleQuestions = shuffleQuestions;
            material.PassPercentage = passPercentage ?? 80;
            material.TestKind = testKind;
            material.QuestionsCountToUse = questionsCountToUse ?? 0;
        }

        _context.CourseMaterials.Update(material);
        await _context.SaveChangesAsync();
        return Json(new { success = true });
    }

    // Вспомогательный метод сохранения файлов, чтобы не дублировать код
    private async Task<string> SaveUploadedFileAsync(IFormFile file, string subFolder)
    {
        string extension = Path.GetExtension(file.FileName).ToLower();
        string uploadsFolder = Path.Combine(_env.WebRootPath, "uploads", "materials", subFolder);

        if (!Directory.Exists(uploadsFolder))
            Directory.CreateDirectory(uploadsFolder);

        string uniqueFileName = Guid.NewGuid().ToString() + extension;
        string fileSavePath = Path.Combine(uploadsFolder, uniqueFileName);

        using (var fileStream = new FileStream(fileSavePath, FileMode.Create))
        {
            // ИСПРАВЛЕНО: Честный асинхронный вызов сохранения файла на диск
            await file.CopyToAsync(fileStream);
        }

        return $"/uploads/materials/{subFolder}/{uniqueFileName}";
    }


    // Вспомогательный метод безопасного удаления файлов с диска
    private void DeletePhysicalFile(string? relativePath)
    {
        if (!string.IsNullOrEmpty(relativePath) && relativePath.StartsWith("/uploads/"))
        {
            string absolutePath = Path.Combine(_env.WebRootPath, relativePath.TrimStart('/'));
            if (System.IO.File.Exists(absolutePath))
            {
                System.IO.File.Delete(absolutePath);
            }
        }
    }

    [HttpPost]
    public async Task<IActionResult> ToggleTestType(int id)
    {
        var material = await _context.CourseMaterials.FindAsync(id);
        if (material == null) return Json(new { success = false });

        material.ContentOrPath = material.ContentOrPath == "Final" ? "Progress" : "Final";
        _context.CourseMaterials.Update(material);
        await _context.SaveChangesAsync();
        return Json(new { success = true });
    }

    [HttpGet]
    [Authorize(Roles = "Обучающийся")] 
    public async Task<IActionResult> StudentView(int courseId, int groupId, int? activeId) // ДОБАВЛЕН groupId
    {
        var course = await _context.Courses
            .Include(c => c.Themes.OrderBy(t => t.Order))
                .ThenInclude(t => t.Materials.OrderBy(m => m.Order))
            .Include(c => c.Materials)
            .FirstOrDefaultAsync(c => c.Id == courseId);

        if (course == null) return NotFound();

        var userIdClaim = User.FindFirst("UserId")?.Value;
        if (int.TryParse(userIdClaim, out int studentId) && studentId > 0)
        {
            var today = DateTime.Today;
            // ПРОВЕРКА ДОСТУПА СТРОГО ДЛЯ КОНКРЕТНОЙ ГРУППЫ
            var hasAccess = await _context.GroupCourseConfigs
                .AnyAsync(gcc => gcc.CourseId == courseId &&
                                 gcc.GroupId == groupId && // ПРОВЕРКА ГРУППЫ
                                 gcc.Group.GroupStudents.Any(gs => gs.StudentId == studentId) &&
                                 (gcc.Group.MaterialAccessDate >= today || 
                                 (gcc.Group.MaterialAccessDate == null && gcc.Group.EndDate >= today)));

            if (!hasAccess) return RedirectToAction("AccessDenied", "Auth");
        }
        else
        {
            return Challenge(); 
        }

        ViewBag.CourseId = course.Id;
        ViewBag.CourseTitle = course.Title;
        ViewBag.GroupId = groupId; // ПРОКИДЫВАЕМ В VIEW
        ViewBag.ActiveId = activeId;

        return View(course); 
    }
}
