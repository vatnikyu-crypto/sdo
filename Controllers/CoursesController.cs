using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models;
using System.IO.Compression;
using System.Text.Json;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SdoApp.Services;

namespace SdoApp.Controllers;

[Authorize(Roles = "Админ")] // Доступ только для Администратора
public class CoursesController : Controller
{
    private readonly AppDbContext _context;
    private readonly IWebHostEnvironment _env;
    private readonly AuditService _auditService;

    public CoursesController(AppDbContext context, IWebHostEnvironment env, AuditService auditService)
    {
        _context = context;
        _env = env;
        _auditService = auditService;
    }

    // GET: /Courses/Index
    // Отображает витрину с карточками курсов
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var courses = await _context.Courses
            .Include(c => c.Materials) // Подтягиваем материалы для счетчика лекций
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();

        return View(courses);
    }

    // GET: /Courses/Create
    [HttpGet]
    public async Task<IActionResult> Create()
    {
        // Загружаем программы Минтруда для комбобокса, сортируя по алфавиту
        ViewBag.MintrudPrograms = await _context.MintrudPrograms
            .OrderBy(p => p.Name)
            .ToListAsync();

        return View();
    }

    // POST: /Courses/Create
    // Обработка формы, привязка программы Минтруда и загрузка картинок на сервер
    [HttpPost]
    public async Task<IActionResult> Create(string title, string? description, IFormFile? imageFile, int? programId, int? durationHours, int? recertificationMonths)
    {
        // Буферизируем текстовые данные на случай ошибки валидации
        ViewBag.Title = title;
        ViewBag.Description = description;

        // Перевызываем наполнение списка, чтобы комбобокс не опустел при возврате страницы с ошибкой
        ViewBag.MintrudPrograms = await _context.MintrudPrograms
            .OrderBy(p => p.Name)
            .ToListAsync();

        if (string.IsNullOrWhiteSpace(title))
        {
            ModelState.AddModelError("", "Ошибка: Название курса обязательно для заполнения!");
            return View();
        }

        string? savedImageUrl = null;
        if (imageFile != null && imageFile.Length > 0)
        {
            var extension = Path.GetExtension(imageFile.FileName).ToLower();
            var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp" };
            if (!allowedExtensions.Contains(extension))
            {
                ModelState.AddModelError("", "Ошибка: Разрешены только форматы изображений (.jpg, .jpeg, .png, .webp)!");
                return View();
            }

            try
            {
                // Путь к папке wwwroot/uploads/courses
                string uploadsFolder = Path.Combine(_env.WebRootPath, "uploads", "courses");
                if (!Directory.Exists(uploadsFolder))
                {
                    Directory.CreateDirectory(uploadsFolder);
                }

                // Уникальное имя файла
                string uniqueFileName = Guid.NewGuid().ToString() + extension;
                string fileSavePath = Path.Combine(uploadsFolder, uniqueFileName);

                using (var fileStream = new FileStream(fileSavePath, FileMode.Create))
                {
                    await imageFile.CopyToAsync(fileStream);
                }

                savedImageUrl = $"/uploads/courses/{uniqueFileName}";
            }
            catch (Exception ex)
            {
                ModelState.AddModelError("", $"Ошибка сохранения файла: {ex.Message}");
                return View();
            }
        }
        else
        {
            // Если картинка не выбрана, ставим заглушку
            savedImageUrl = "/uploads/courses/default-course.jpeg";
        }

        // Создаем новый курс с привязкой к программе Минтруда
        var newCourse = new Course
        {
            Title = title.Trim(),
            Description = description?.Trim(),
            ImageUrl = savedImageUrl,
            CreatedAt = DateTime.UtcNow,
            MintrudProgramId = programId,
            DurationHours = durationHours,
            RecertificationMonths = recertificationMonths
        };

        _context.Courses.Add(newCourse);
        await _auditService.LogAsync("Create", "Course", newCourse.Id, $"Создан курс: {newCourse.Title}");
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    // POST: /Courses/Delete/5
    // УМНОЕ КАСКАДНОЕ УДАЛЕНИЕ: Стирает курс, обложку и ВСЕ физические файлы лекций/видео с диска!
    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        // Загружаем курс вместе со ВСЕМИ его вложенными материалами
        var course = await _context.Courses
            .Include(c => c.Materials)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (course == null)
        {
            return Json(new { success = false, message = "Запрашиваемый учебный курс не найден в СДО!" });
        }

        try
        {
            // 1. ЦИКЛ ФИЗИЧЕСКОЙ ОЧИСТКИ: Стираем файлы абсолютно всех лекций, видео и заданий этого курса
            foreach (var material in course.Materials)
            {
                if (!string.IsNullOrEmpty(material.ContentOrPath) && material.ContentOrPath.StartsWith("/uploads/"))
                {
                    // Для практических заданий отсекаем текст инструкции перед разделителем |||
                    string filePath = material.Type == MaterialType.Assignment
                        ? material.ContentOrPath.Split("|||")[0]
                        : material.ContentOrPath;

                    if (!string.IsNullOrEmpty(filePath) && filePath.StartsWith("/uploads/"))
                    {
                        string absoluteMaterialPath = Path.Combine(_env.WebRootPath, filePath.TrimStart('/'));
                        if (System.IO.File.Exists(absoluteMaterialPath))
                        {
                            System.IO.File.Delete(absoluteMaterialPath);
                        }
                    }
                }
            }

            // 2. УДАЛЕНИЕ ОБЛОЖКИ КУРСА
            if (!string.IsNullOrEmpty(course.ImageUrl) && !course.ImageUrl.Contains("default-course.jpeg"))
            {
                string absoluteImagePath = Path.Combine(_env.WebRootPath, course.ImageUrl.TrimStart('/'));
                if (System.IO.File.Exists(absoluteImagePath))
                {
                    System.IO.File.Delete(absoluteImagePath);
                }
            }

            // 3. УДАЛЕНИЕ ИЗ БАЗЫ: EF Core сам каскадно удалит строчки материалов из таблицы CourseMaterials,
            // так как они привязаны к этому CourseId!
            _context.Courses.Remove(course);
            await _context.SaveChangesAsync();
            await _auditService.LogAsync("Delete", "Course", course.Id, $"Удален курс: {course.Title}");
            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Ошибка при полном каскадном удалении курса: {ex.Message}" });
        }
    }

    // =================================================================================
    // ЭКСПОРТ КУРСА В ZIP (полный бэкап: карточка + темы + материалы + тесты + шаблоны + файлы)
    // =================================================================================
    [HttpGet]
    public async Task<IActionResult> ExportCourse(int id)
    {
        var course = await _context.Courses
            .Include(c => c.Themes)
            .Include(c => c.Materials)
            .Include(c => c.DocumentTemplates)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (course == null) return NotFound("Курс не найден.");

        using var ms = new MemoryStream();

        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var addedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // ============================================================
            // 1. МЕТАДАННЫЕ КУРСА (course.json)
            // ============================================================
            var themesForJson = course.Themes.OrderBy(t => t.Order).Select(t => new
            {
                t.Title,
                t.Order
            }).ToList();

            // Загружаем вопросы для всех тестов одним запросом
            var testMaterialIds = course.Materials
                .Where(m => m.Type == MaterialType.Test)
                .Select(m => m.Id)
                .ToList();

            var allQuestions = new List<TestQuestion>();
            if (testMaterialIds.Any())
            {
                allQuestions = await _context.TestQuestions
                    .Include(q => q.Answers)
                    .Where(q => testMaterialIds.Contains(q.CourseMaterialId))
                    .ToListAsync();
            }

            var themesOrdered = course.Themes.OrderBy(t => t.Order).ToList();

            var materialsForJson = course.Materials.OrderBy(m => m.Order).Select(m =>
            {
                // Разбираем ContentOrPath
                string? filePath = null;
                string? textContent = null;
                bool isExternal = false;

                if (!string.IsNullOrEmpty(m.ContentOrPath))
                {
                    if (m.Type == MaterialType.Text)
                    {
                        textContent = m.ContentOrPath;
                    }
                    else if (m.Type == MaterialType.Assignment)
                    {
                        var parts = m.ContentOrPath.Split("|||");
                        filePath = parts.Length > 0 && !string.IsNullOrEmpty(parts[0]) ? parts[0] : null;
                        textContent = parts.Length > 1 ? parts[1] : null;
                    }
                    else if (m.Type == MaterialType.Pdf || m.Type == MaterialType.Video || m.Type == MaterialType.Image)
                    {
                        filePath = m.ContentOrPath;
                        isExternal = !filePath.StartsWith("/uploads/");
                    }
                    else if (m.Type == MaterialType.Scorm)
                    {
                        filePath = m.ContentOrPath;
                    }
                }

                // Вопросы теста
                var questions = m.Type == MaterialType.Test
                    ? allQuestions.Where(q => q.CourseMaterialId == m.Id).Select(q => new
                    {
                        q.QuestionText,
                        q.Hint,
                        q.IsMultipleChoice,
                        Answers = q.Answers.Select(a => new
                        {
                            a.AnswerText,
                            a.IsCorrect,
                            a.ImageUrl
                        }).ToList()
                    }).ToList()
                    : null;

                return new
                {
                    m.Title,
                    Type = m.Type.ToString(),
                    m.Order,
                    ThemeIndex = m.ThemeId.HasValue                            // ← ИСПРАВЛЕНО
            ? (int?)themesOrdered.FindIndex(t => t.Id == m.ThemeId.Value)
            : null,
                    m.TimeLimit,
                    m.ShuffleQuestions,
                    m.PassPercentage,
                    TestKind = m.TestKind.ToString(),
                    m.QuestionsCountToUse,
                    FilePath = filePath,
                    IsExternal = isExternal,
                    TextContent = textContent,
                    Questions = questions
                };
            }).ToList();

            var courseJson = new
            {
                course.Title,
                course.Description,
                course.DurationHours,
                course.RecertificationMonths,
                course.MintrudProgramId,
                ExportedAt = DateTime.UtcNow,
                ImageUrl = course.ImageUrl,
                Themes = themesForJson,
                Materials = materialsForJson,
                DocumentTemplates = course.DocumentTemplates.OrderBy(t => t.SortOrder).Select(t => new
                {
                    t.Name,
                    t.FilePath,
                    t.IsPerStudent,
                    t.RequiresCommission,
                    t.SortOrder
                }).ToList()
            };

            var metaEntry = zip.CreateEntry("course.json", CompressionLevel.Optimal);
            using (var s = metaEntry.Open())
            using (var w = new StreamWriter(s, System.Text.Encoding.UTF8))
            {
                var json = System.Text.Json.JsonSerializer.Serialize(courseJson,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await w.WriteAsync(json);
            }

            // ============================================================
            // 2. ОБЛОЖКА КУРСА
            // ============================================================
            if (!string.IsNullOrEmpty(course.ImageUrl)
                && course.ImageUrl.StartsWith("/uploads/")
                && !course.ImageUrl.Contains("default-course.jpeg"))
            {
                AddFileToZip(zip, course.ImageUrl, addedEntries);
            }

            // ============================================================
            // 3. ФАЙЛЫ МАТЕРИАЛОВ
            // ============================================================
            foreach (var m in course.Materials)
            {
                if (string.IsNullOrEmpty(m.ContentOrPath)) continue;

                if (m.Type == MaterialType.Assignment)
                {
                    var fileUrl = m.ContentOrPath.Split("|||")[0];
                    if (!string.IsNullOrEmpty(fileUrl) && fileUrl.StartsWith("/uploads/"))
                        AddFileToZip(zip, fileUrl, addedEntries);
                }
                else if (m.Type == MaterialType.Pdf || m.Type == MaterialType.Video || m.Type == MaterialType.Image)
                {
                    // Только локальные файлы, не внешние URL
                    if (m.ContentOrPath.StartsWith("/uploads/"))
                        AddFileToZip(zip, m.ContentOrPath, addedEntries);
                }
                else if (m.Type == MaterialType.Scorm)
                {
                    // SCORM — копируем всю папку рекурсивно
                    if (m.ContentOrPath.StartsWith("/uploads/materials/scorm/"))
                    {
                        var parts = m.ContentOrPath.Split('/');
                        if (parts.Length > 4)
                        {
                            string folderName = parts[4];
                            string scormFolder = Path.Combine(_env.WebRootPath, "uploads", "materials", "scorm", folderName);
                            if (Directory.Exists(scormFolder))
                            {
                                foreach (var file in Directory.GetFiles(scormFolder, "*", SearchOption.AllDirectories))
                                {
                                    string rel = Path.GetRelativePath(_env.WebRootPath, file).Replace('\\', '/');
                                    string entryName = $"files/{rel}";
                                    if (!addedEntries.Add(entryName)) continue;
                                    zip.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
                                }
                            }
                        }
                    }
                }

                // Картинки вариантов ответов теста
                if (m.Type == MaterialType.Test)
                {
                    var questions = allQuestions.Where(q => q.CourseMaterialId == m.Id);
                    foreach (var q in questions)
                    {
                        foreach (var a in q.Answers)
                        {
                            if (!string.IsNullOrEmpty(a.ImageUrl) && a.ImageUrl.StartsWith("/uploads/"))
                                AddFileToZip(zip, a.ImageUrl, addedEntries);
                        }
                    }
                }
            }

            // ============================================================
            // 4. ШАБЛОНЫ ДОКУМЕНТОВ
            // ============================================================
            foreach (var t in course.DocumentTemplates)
            {
                if (!string.IsNullOrEmpty(t.FilePath) && t.FilePath.StartsWith("/uploads/"))
                    AddFileToZip(zip, t.FilePath, addedEntries);
            }
        }

        // Безопасное имя файла
        string safeName = System.Text.RegularExpressions.Regex.Replace(
            course.Title, @"[\\/:*?""<>|]", "").Trim();
        if (string.IsNullOrEmpty(safeName)) safeName = $"Course_{course.Id}";

        string fileName = $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.zip";

        // Правильное кодирование русского имени (RFC 5987)
        var cd = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
        {
            FileNameStar = fileName
        };
        Response.Headers[Microsoft.Net.Http.Headers.HeaderNames.ContentDisposition] = cd.ToString();

        return File(ms.ToArray(), "application/zip");
    }

    // Хелпер: добавляет один файл в zip, сохраняя относительный путь
    // Хелпер: добавляет один файл в zip, сохраняя относительный путь
    // addedEntries — множество уже добавленных путей, чтобы не дублировать файлы
    private void AddFileToZip(ZipArchive zip, string relativeUrl, HashSet<string> addedEntries)
    {
        string absPath = Path.Combine(_env.WebRootPath, relativeUrl.TrimStart('/'));
        if (!System.IO.File.Exists(absPath)) return;

        string entryName = $"files/{relativeUrl.TrimStart('/')}";

        // Дубликаты не добавляем
        if (!addedEntries.Add(entryName)) return;

        zip.CreateEntryFromFile(absPath, entryName, CompressionLevel.Optimal);
    }

    // =================================================================================
    // ИМПОРТ КУРСА ИЗ ZIP (симметрично ExportCourse)
    // =================================================================================
    [HttpPost]
    [RequestSizeLimit(500L * 1024 * 1024)]
    public async Task<IActionResult> ImportCourse(IFormFile zipFile)
    {
        if (zipFile == null || zipFile.Length == 0)
            return Json(new { success = false, message = "Файл не выбран." });

        if (Path.GetExtension(zipFile.FileName).ToLower() != ".zip")
            return Json(new { success = false, message = "Требуется ZIP-архив." });

        using var memStream = new MemoryStream();
        await zipFile.CopyToAsync(memStream);
        memStream.Position = 0;

        using var archive = new ZipArchive(memStream, ZipArchiveMode.Read);

        // 1. Ищем course.json
        var metaEntry = archive.GetEntry("course.json");
        if (metaEntry == null)
            return Json(new { success = false, message = "В архиве отсутствует course.json. Это не бэкап СДО." });

        // 2. Парсим метаданные
        JsonDocument doc;
        using (var s = metaEntry.Open())
        {
            doc = await JsonDocument.ParseAsync(s);
        }

        // Кэш «старый путь → новый путь» — чтобы один и тот же файл не копировался дважды
        var importedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using (doc)
        {
            var root = doc.RootElement;

            await using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
                // ==========================================
                // 3. СОЗДАЁМ КУРС
                // ==========================================
                var newCourse = new Course
                {
                    Title = $"{root.GetProperty("Title").GetString()} (импорт {DateTime.Now:dd.MM.yyyy HH:mm})",
                    Description = root.TryGetProperty("Description", out var desc) ? desc.GetString() : null,
                    DurationHours = root.TryGetProperty("DurationHours", out var dh) && dh.ValueKind != JsonValueKind.Null
                        ? dh.GetInt32() : null,
                    RecertificationMonths = root.TryGetProperty("RecertificationMonths", out var rm) && rm.ValueKind != JsonValueKind.Null
                        ? rm.GetInt32() : null,
                    MintrudProgramId = null,
                    CreatedAt = DateTime.UtcNow
                };

                // Проверка MintrudProgramId: если в БД нет такой программы — null
                if (root.TryGetProperty("MintrudProgramId", out var mp) && mp.ValueKind != JsonValueKind.Null)
                {
                    int mpId = mp.GetInt32();
                    if (await _context.MintrudPrograms.AnyAsync(p => p.Id == mpId))
                        newCourse.MintrudProgramId = mpId;
                }

                // Обложка
                string? oldImageUrl = root.TryGetProperty("ImageUrl", out var iu) ? iu.GetString() : null;
                if (!string.IsNullOrEmpty(oldImageUrl))
                {
                    newCourse.ImageUrl = await ImportFileFromZipAsync(
                        archive, oldImageUrl, "uploads/courses", "default-course.jpeg", importedPaths);
                }

                _context.Courses.Add(newCourse);
                await _context.SaveChangesAsync();

                // ==========================================
                // 4. ТЕМЫ
                // ==========================================
                var themeIds = new List<int>();
                if (root.TryGetProperty("Themes", out var themesEl) && themesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in themesEl.EnumerateArray())
                    {
                        var theme = new CourseTheme
                        {
                            CourseId = newCourse.Id,
                            Title = t.GetProperty("Title").GetString() ?? "",
                            Order = t.GetProperty("Order").GetInt32()
                        };
                        _context.CourseThemes.Add(theme);
                        await _context.SaveChangesAsync();
                        themeIds.Add(theme.Id);
                    }
                }

                // ==========================================
                // 5. МАТЕРИАЛЫ
                // ==========================================
                if (root.TryGetProperty("Materials", out var matsEl) && matsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in matsEl.EnumerateArray())
                    {
                        string typeStr = m.GetProperty("Type").GetString() ?? "Text";
                        if (!Enum.TryParse<MaterialType>(typeStr, out var mType)) mType = MaterialType.Text;

                        // Маппинг ThemeIndex → реальный ThemeId
                        int? themeIndex = null;
                        if (m.TryGetProperty("ThemeIndex", out var ti) && ti.ValueKind != JsonValueKind.Null)
                        {
                            int idx = ti.GetInt32();
                            if (idx >= 0 && idx < themeIds.Count) themeIndex = idx;
                        }

                        var material = new CourseMaterial
                        {
                            CourseId = newCourse.Id,
                            Title = m.GetProperty("Title").GetString() ?? "",
                            Type = mType,
                            Order = m.GetProperty("Order").GetInt32(),
                            ThemeId = themeIndex.HasValue ? themeIds[themeIndex.Value] : null,
                            TimeLimit = m.TryGetProperty("TimeLimit", out var tl) ? tl.GetInt32() : 0,
                            ShuffleQuestions = m.TryGetProperty("ShuffleQuestions", out var sq) && sq.GetBoolean(),
                            PassPercentage = m.TryGetProperty("PassPercentage", out var pp) ? pp.GetInt32() : 80,
                            QuestionsCountToUse = m.TryGetProperty("QuestionsCountToUse", out var qc) ? qc.GetInt32() : 0
                        };

                        if (m.TryGetProperty("TestKind", out var tk) && Enum.TryParse<TestStatus>(tk.GetString(), out var testKind))
                            material.TestKind = testKind;

                        string? filePath = m.TryGetProperty("FilePath", out var fp) ? fp.GetString() : null;
                        string? textContent = m.TryGetProperty("TextContent", out var tc) ? tc.GetString() : null;
                        bool isExternal = m.TryGetProperty("IsExternal", out var ie) && ie.GetBoolean();

                        switch (mType)
                        {
                            case MaterialType.Text:
                                material.ContentOrPath = textContent ?? "";
                                break;

                            case MaterialType.Pdf:
                            case MaterialType.Video:
                            case MaterialType.Image:
                                if (isExternal && !string.IsNullOrEmpty(filePath))
                                {
                                    material.ContentOrPath = filePath; // внешний URL — как есть
                                }
                                else if (!string.IsNullOrEmpty(filePath))
                                {
                                    string subFolder = mType == MaterialType.Pdf ? "uploads/materials/pdfs"
                                                     : mType == MaterialType.Video ? "uploads/materials/videos"
                                                     : "uploads/materials/images";
                                    material.ContentOrPath = await ImportFileFromZipAsync(
                                        archive, filePath, subFolder, null, importedPaths);
                                }
                                break;

                            case MaterialType.Assignment:
                                string newFileUrl = "";
                                if (!string.IsNullOrEmpty(filePath))
                                {
                                    newFileUrl = await ImportFileFromZipAsync(
                                        archive, filePath, "uploads/materials/assignments", null, importedPaths) ?? "";
                                }
                                material.ContentOrPath = $"{newFileUrl}|||{textContent ?? ""}";
                                break;

                            case MaterialType.Scorm:
                                if (!string.IsNullOrEmpty(filePath))
                                {
                                    material.ContentOrPath = await ImportScormFromZipAsync(archive, filePath);
                                }
                                break;
                        }

                        _context.CourseMaterials.Add(material);
                        await _context.SaveChangesAsync();

                        // Вопросы теста
                        if (mType == MaterialType.Test
                            && m.TryGetProperty("Questions", out var qsEl)
                            && qsEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var q in qsEl.EnumerateArray())
                            {
                                var question = new TestQuestion
                                {
                                    CourseMaterialId = material.Id,
                                    QuestionText = q.GetProperty("QuestionText").GetString() ?? "",
                                    Hint = q.TryGetProperty("Hint", out var h) ? h.GetString() : null,
                                    IsMultipleChoice = q.TryGetProperty("IsMultipleChoice", out var imc) && imc.GetBoolean()
                                };
                                _context.TestQuestions.Add(question);
                                await _context.SaveChangesAsync();

                                if (q.TryGetProperty("Answers", out var ansEl) && ansEl.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var a in ansEl.EnumerateArray())
                                    {
                                        string? oldImg = a.TryGetProperty("ImageUrl", out var aimg) ? aimg.GetString() : null;
                                        string? newImg = null;
                                        if (!string.IsNullOrEmpty(oldImg))
                                        {
                                            newImg = await ImportFileFromZipAsync(
                                                archive, oldImg, "uploads/materials/test_options", null, importedPaths);
                                        }

                                        _context.TestAnswerOptions.Add(new TestAnswerOption
                                        {
                                            TestQuestionId = question.Id,
                                            AnswerText = a.GetProperty("AnswerText").GetString() ?? "",
                                            IsCorrect = a.TryGetProperty("IsCorrect", out var ic) && ic.GetBoolean(),
                                            ImageUrl = newImg
                                        });
                                    }
                                    await _context.SaveChangesAsync();
                                }
                            }
                        }
                    }
                }

                // ==========================================
                // 6. ШАБЛОНЫ ДОКУМЕНТОВ
                // ==========================================
                if (root.TryGetProperty("DocumentTemplates", out var tmplsEl) && tmplsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in tmplsEl.EnumerateArray())
                    {
                        string? oldFilePath = t.TryGetProperty("FilePath", out var tfp) ? tfp.GetString() : null;
                        string? newFilePath = null;
                        if (!string.IsNullOrEmpty(oldFilePath))
                        {
                            newFilePath = await ImportFileFromZipAsync(
                                archive, oldFilePath, "uploads/templates", null, importedPaths);
                        }

                        _context.CourseDocumentTemplates.Add(new CourseDocumentTemplate
                        {
                            CourseId = newCourse.Id,
                            Name = t.GetProperty("Name").GetString() ?? "",
                            FilePath = newFilePath,
                            IsPerStudent = t.TryGetProperty("IsPerStudent", out var ips) && ips.GetBoolean(),
                            RequiresCommission = t.TryGetProperty("RequiresCommission", out var rc) && rc.GetBoolean(),
                            SortOrder = t.TryGetProperty("SortOrder", out var so) ? so.GetInt32() : 0
                        });
                    }
                    await _auditService.LogAsync("Import", "Course", newCourse.Id, $"Импортирован курс из ZIP: {newCourse.Title}");
                    await _context.SaveChangesAsync();
                }

                await tx.CommitAsync();
                return Json(new { success = true, courseId = newCourse.Id });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return Json(new { success = false, message = $"Ошибка импорта: {ex.Message}" });
            }
        }
    }

    // =================================================================================
    // ХЕЛПЕР: копирует один файл из ZIP в wwwroot с новым GUID-именем
    // oldUrl       — путь внутри архива, например /uploads/materials/pdfs/xxx.pdf
    // targetFolder — куда копировать, например uploads/materials/pdfs
    // skipIfContains — если oldUrl содержит подстроку — вернуть oldUrl (для default-course.jpeg)
    // importedPaths — кэш «старый путь → новый путь» для дедупликации
    // =================================================================================
    private async Task<string?> ImportFileFromZipAsync(
        ZipArchive archive,
        string oldUrl,
        string targetFolder,
        string? skipIfContains,
        Dictionary<string, string> importedPaths)
    {
        if (string.IsNullOrEmpty(oldUrl)) return null;
        if (!oldUrl.StartsWith("/uploads/")) return oldUrl;

        // Уже импортировали этот файл (дубликат в разных материалах) — возвращаем новый путь
        if (importedPaths.TryGetValue(oldUrl, out var cached))
            return cached;

        // Заглушка (default-course.jpeg) — оставляем как есть
        if (!string.IsNullOrEmpty(skipIfContains) && oldUrl.Contains(skipIfContains))
            return oldUrl;

        // Ищем файл в архиве (путь внутри ZIP: files/uploads/...)
        string entryName = $"files{oldUrl}";
        var entry = archive.GetEntry(entryName);
        if (entry == null) return null; // файла нет — пропускаем

        // Генерируем новое имя с GUID
        string ext = Path.GetExtension(oldUrl).ToLower();
        string newName = Guid.NewGuid().ToString() + ext;

        string targetDir = Path.Combine(_env.WebRootPath, targetFolder);
        if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

        string fullPath = Path.Combine(targetDir, newName);
        using (var entryStream = entry.Open())
        using (var fs = new FileStream(fullPath, FileMode.Create))
        {
            await entryStream.CopyToAsync(fs);
        }

        string newUrl = $"/{targetFolder.TrimStart('/')}/{newName}";
        importedPaths[oldUrl] = newUrl;
        return newUrl;
    }

    // =================================================================================
    // ХЕЛПЕР: копирует SCORM-папку из ZIP в wwwroot с новой папкой
    // oldUrl = /uploads/materials/scorm/{oldFolder}/index.html
    // Возвращает /uploads/materials/scorm/{newFolder}/index.html
    // =================================================================================
    private async Task<string?> ImportScormFromZipAsync(ZipArchive archive, string oldUrl)
    {
        if (string.IsNullOrEmpty(oldUrl) || !oldUrl.StartsWith("/uploads/materials/scorm/"))
            return oldUrl;

        var parts = oldUrl.Split('/');
        if (parts.Length < 5) return null;

        string oldFolder = parts[4];
        string entryPoint = string.Join("/", parts.Skip(5));

        string oldPrefix = $"files/uploads/materials/scorm/{oldFolder}/";
        string newFolderName = Guid.NewGuid().ToString();
        string newDir = Path.Combine(_env.WebRootPath, "uploads", "materials", "scorm", newFolderName);
        Directory.CreateDirectory(newDir);

        // Копируем все записи из архива, начинающиеся с oldPrefix
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            string relPath = entry.FullName.Substring(oldPrefix.Length);
            if (string.IsNullOrEmpty(relPath)) continue; // сам каталог

            string destFile = Path.Combine(newDir, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);

            using (var src = entry.Open())
            using (var fs = new FileStream(destFile, FileMode.Create))
            {
                await src.CopyToAsync(fs);
            }
        }

        return $"/uploads/materials/scorm/{newFolderName}/{entryPoint}";
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        // Ищем КУРС, а не группу!
        var course = await _context.Courses
            .Include(c => c.DocumentTemplates)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (course == null) return NotFound();

        // Заполняем ViewBag для страницы
        ViewBag.Id = course.Id;
        ViewBag.CourseTitle = course.Title;
        ViewBag.Description = course.Description;
        ViewBag.ImageUrl = course.ImageUrl;
        ViewBag.DurationHours = course.DurationHours;
        ViewBag.RecertificationMonths = course.RecertificationMonths;
        ViewBag.DocumentTemplates = course.DocumentTemplates.OrderBy(t => t.SortOrder).ToList();
        ViewBag.CurrentMintrudProgramId = course.MintrudProgramId;

        // Загружаем программы Минтруда
        ViewBag.MintrudPrograms = await _context.MintrudPrograms.OrderBy(p => p.Name).ToListAsync();

        return View();
    }

    // POST: /Courses/Edit/5
    [HttpPost]
    public async Task<IActionResult> Edit(
        int id,
        string title,
        string? description,
        IFormFile? imageFile,
        int? programId,
        int? durationHours,
        int? recertificationMonths,
        List<CourseDocumentTemplateInput> templates) // <-- Принимаем список шаблонов из формы
    {
        var course = await _context.Courses
            .Include(c => c.DocumentTemplates) // Обязательно подтягиваем старые шаблоны для обновления
            .FirstOrDefaultAsync(c => c.Id == id);

        if (course == null) return NotFound();

        // Буферизация данных для возврата в View в случае ошибок валидации
        ViewBag.Id = course.Id;
        ViewBag.CourseTitle = title;
        ViewBag.Description = description;
        ViewBag.ImageUrl = course.ImageUrl;
        ViewBag.DurationHours = durationHours;
        ViewBag.RecertificationMonths = recertificationMonths;
        ViewBag.CurrentMintrudProgramId = programId;
        ViewBag.MintrudPrograms = await _context.MintrudPrograms.OrderBy(p => p.Name).ToListAsync();

        if (string.IsNullOrWhiteSpace(title))
        {
            ModelState.AddModelError("", "Ошибка: Название курса является обязательным полем!");
            return View(course);
        }

        // 1. ОБРАБОТКА ОБЛОЖКИ КУРСА
        if (imageFile != null && imageFile.Length > 0)
        {
            var ext = Path.GetExtension(imageFile.FileName).ToLower();
            var allowedExts = new[] { ".jpg", ".jpeg", ".png", ".webp" };
            if (!allowedExts.Contains(ext))
            {
                ModelState.AddModelError("", "Ошибка обложки: Разрешены только форматы .jpg, .jpeg, .png, .webp!");
                return View(course);
            }

            try
            {
                string uploadsFolder = Path.Combine(_env.WebRootPath, "uploads", "courses");
                if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

                string uniqueFileName = Guid.NewGuid().ToString() + ext;
                string fileSavePath = Path.Combine(uploadsFolder, uniqueFileName);

                using (var fileStream = new FileStream(fileSavePath, FileMode.Create))
                {
                    await imageFile.CopyToAsync(fileStream);
                }

                if (!string.IsNullOrEmpty(course.ImageUrl) && !course.ImageUrl.Contains("default-course.jpeg"))
                {
                    string oldPath = Path.Combine(_env.WebRootPath, course.ImageUrl.TrimStart('/'));
                    if (System.IO.File.Exists(oldPath)) System.IO.File.Delete(oldPath);
                }

                course.ImageUrl = $"/uploads/courses/{uniqueFileName}";
            }
            catch (Exception ex)
            {
                ModelState.AddModelError("", $"Ошибка при обновлении обложки: {ex.Message}");
                return View(course);
            }
        }

        // 2. ОБРАБОТКА ШАБЛОНОВ ДОКУМЕНТОВ
        // Сначала удаляем файлы старых шаблонов, которые админ решил убрать
        var inputIds = templates?.Select(t => t.Id).Where(id => id > 0).ToList() ?? new List<int>();
        var templatesToRemove = course.DocumentTemplates.Where(t => !inputIds.Contains(t.Id)).ToList();

        foreach (var oldTemp in templatesToRemove)
        {
            if (!string.IsNullOrEmpty(oldTemp.FilePath))
            {
                string oldFilePath = Path.Combine(_env.WebRootPath, oldTemp.FilePath.TrimStart('/'));
                if (System.IO.File.Exists(oldFilePath)) System.IO.File.Delete(oldFilePath);
            }
            _context.CourseDocumentTemplates.Remove(oldTemp);
        }

        // Папка для хранения шаблонов документов Word/Excel
        string docFolder = Path.Combine(_env.WebRootPath, "uploads", "templates");
        if (!Directory.Exists(docFolder)) Directory.CreateDirectory(docFolder);

        if (templates != null)
        {
            for (int i = 0; i < templates.Count; i++)
            {
                var tInput = templates[i];

                // Если это абсолютно пустой блок без имени — игнорируем его
                if (string.IsNullOrWhiteSpace(tInput.Name)) continue;

                string? currentFilePath = tInput.ExistingFilePath;

                // Если загружен новый файл для этого шаблона
                if (tInput.File != null && tInput.File.Length > 0)
                {
                    var fileExt = Path.GetExtension(tInput.File.FileName).ToLower();
                    var allowedDocExts = new[] { ".docx", ".xlsx" };

                    if (!allowedDocExts.Contains(fileExt))
                    {
                        ModelState.AddModelError("", $"Ошибка в документе '{tInput.Name}': Разрешены только файлы .docx и .xlsx!");
                        return View(course);
                    }

                    // Удаляем старый файл шаблона, если он заменяется новым
                    if (!string.IsNullOrEmpty(currentFilePath))
                    {
                        string oldFile = Path.Combine(_env.WebRootPath, currentFilePath.TrimStart('/'));
                        if (System.IO.File.Exists(oldFile)) System.IO.File.Delete(oldFile);
                    }

                    // Сохраняем новый файл документа
                    string uniqueDocName = Guid.NewGuid().ToString() + fileExt;
                    string docSavePath = Path.Combine(docFolder, uniqueDocName);

                    using (var stream = new FileStream(docSavePath, FileMode.Create))
                    {
                        await tInput.File.CopyToAsync(stream);
                    }
                    currentFilePath = $"/uploads/templates/{uniqueDocName}";
                }

                if (tInput.Id > 0)
                {
                    // Редактирование существующего шаблона
                    var existingTemplate = course.DocumentTemplates.FirstOrDefault(t => t.Id == tInput.Id);
                    if (existingTemplate != null)
                    {
                        existingTemplate.Name = tInput.Name.Trim();
                        existingTemplate.IsPerStudent = tInput.IsPerStudent;
                        existingTemplate.RequiresCommission = tInput.RequiresCommission;
                        existingTemplate.SortOrder = i; // Сохраняем актуальный порядок после перетаскивания
                        existingTemplate.FilePath = currentFilePath;
                        _context.CourseDocumentTemplates.Update(existingTemplate);
                    }
                }
                else
                {
                    // Создание нового шаблона
                    var newTemplate = new CourseDocumentTemplate
                    {
                        Name = tInput.Name.Trim(),
                        IsPerStudent = tInput.IsPerStudent,
                        RequiresCommission = tInput.RequiresCommission,
                        SortOrder = i,
                        FilePath = currentFilePath,
                        CourseId = course.Id
                    };
                    _context.CourseDocumentTemplates.Add(newTemplate);
                }
            }
        }

        // 3. ОБНОВЛЕНИЕ ОСНОВНЫХ ДАННЫХ КУРСА
        course.Title = title.Trim();
        course.Description = description?.Trim();
        course.MintrudProgramId = programId;
        course.DurationHours = durationHours;
        course.RecertificationMonths = recertificationMonths;

        _context.Courses.Update(course);
        await _auditService.LogAsync("Update", "Course", course.Id, $"Изменены настройки курса: {course.Title}");
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    // POST: /Courses/CreateMintrudProgram
    [HttpPost]
    public async Task<IActionResult> CreateMintrudProgram(string name, string code, string numberProgram)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Json(new { success = false, message = "Название не может быть пустым" });

        var program = new MintrudProgram
        {
            Name = name.Trim(),
            Code = code?.Trim() ?? string.Empty,
            NumberProgram = numberProgram?.Trim() ?? string.Empty
        };

        _context.MintrudPrograms.Add(program);
        await _context.SaveChangesAsync();

        return Json(new { success = true, id = program.Id, name = program.Name, code = program.Code, numberProgram = program.NumberProgram });
    }

    // POST: /Courses/EditMintrudProgram
    [HttpPost]
    public async Task<IActionResult> EditMintrudProgram(int id, string name, string code, string numberProgram)
    {
        var program = await _context.MintrudPrograms.FindAsync(id);
        if (program == null)
            return Json(new { success = false, message = "Программа не найдена" });

        if (string.IsNullOrWhiteSpace(name))
            return Json(new { success = false, message = "Название не может быть пустым" });

        program.Name = name.Trim();
        program.Code = code?.Trim() ?? string.Empty;
        program.NumberProgram = numberProgram?.Trim() ?? string.Empty;

        _context.MintrudPrograms.Update(program);
        await _context.SaveChangesAsync();

        return Json(new { success = true, id = program.Id, name = program.Name, code = program.Code, numberProgram = program.NumberProgram });
    }

    // POST: /Courses/DeleteMintrudProgram
    [HttpPost]
    public async Task<IActionResult> DeleteMintrudProgram(int id)
    {
        var program = await _context.MintrudPrograms.FindAsync(id);
        if (program == null)
            return Json(new { success = false, message = "Программа не найдена" });

        // Проверяем, не привязана ли программа к курсам, чтобы не нарушить целостность
        bool hasCourses = await _context.Courses.AnyAsync(c => c.MintrudProgramId == id);
        if (hasCourses)
            return Json(new { success = false, message = "Нельзя удалить: эта программа уже привязана к существующим курсам!" });

        _context.MintrudPrograms.Remove(program);
        await _context.SaveChangesAsync();

        return Json(new { success = true });
    }

    [HttpPost]
    public async Task<IActionResult> Duplicate(int id)
    {
        var source = await _context.Courses
            .Include(c => c.Themes)
            .Include(c => c.Materials)
            .Include(c => c.DocumentTemplates)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (source == null)
            return Json(new { success = false, message = "Исходный курс не найден!" });

        await using var tx = await _context.Database.BeginTransactionAsync();
        try
        {
            // ---------- 1. КАРТОЧКА КУРСА ----------
            var newCourse = new Course
            {
                Title = $"{source.Title} (копия от {DateTime.Now:dd.MM.yyyy HH:mm})",
                Description = source.Description,
                ImageUrl = CopyPhysicalFile(source.ImageUrl),
                CreatedAt = DateTime.UtcNow,
                DurationHours = source.DurationHours,
                RecertificationMonths = source.RecertificationMonths,
                MintrudProgramId = source.MintrudProgramId
            };
            _context.Courses.Add(newCourse);
            await _context.SaveChangesAsync();

            // ---------- 2. ТЕМЫ (с маппингом старый Id → новый Id) ----------
            var themeMap = new Dictionary<int, int>();
            foreach (var theme in source.Themes.OrderBy(t => t.Order))
            {
                var newTheme = new CourseTheme
                {
                    CourseId = newCourse.Id,
                    Title = theme.Title,
                    Order = theme.Order
                };
                _context.CourseThemes.Add(newTheme);
                await _context.SaveChangesAsync();
                themeMap[theme.Id] = newTheme.Id;
            }

            // ---------- 3. МАТЕРИАЛЫ КУРСА ----------
            int materialsCount = 0;
            foreach (var material in source.Materials.OrderBy(m => m.Order))
            {
                var newMaterial = new CourseMaterial
                {
                    CourseId = newCourse.Id,
                    Title = material.Title,
                    Type = material.Type,
                    Order = material.Order,
                    TimeLimit = material.TimeLimit,
                    ShuffleQuestions = material.ShuffleQuestions,
                    PassPercentage = material.PassPercentage,
                    TestKind = material.TestKind,
                    QuestionsCountToUse = material.QuestionsCountToUse,
                    ThemeId = material.ThemeId.HasValue && themeMap.ContainsKey(material.ThemeId.Value)
                        ? themeMap[material.ThemeId.Value]
                        : null,
                    ContentOrPath = CopyMaterialContent(material)
                };
                _context.CourseMaterials.Add(newMaterial);
                await _context.SaveChangesAsync();
                materialsCount++;

                // ---------- 4. ВОПРОСЫ И ОТВЕТЫ ДЛЯ ТЕСТОВ ----------
                if (material.Type == MaterialType.Test)
                {
                    var sourceQuestions = await _context.TestQuestions
                        .Include(q => q.Answers)
                        .Where(q => q.CourseMaterialId == material.Id)
                        .ToListAsync();

                    foreach (var q in sourceQuestions)
                    {
                        var newQuestion = new TestQuestion
                        {
                            CourseMaterialId = newMaterial.Id,
                            QuestionText = q.QuestionText,
                            Hint = q.Hint,
                            IsMultipleChoice = q.IsMultipleChoice
                        };
                        _context.TestQuestions.Add(newQuestion);
                        await _context.SaveChangesAsync();

                        foreach (var a in q.Answers)
                        {
                            _context.TestAnswerOptions.Add(new TestAnswerOption
                            {
                                TestQuestionId = newQuestion.Id,
                                AnswerText = a.AnswerText,
                                IsCorrect = a.IsCorrect,
                                ImageUrl = CopyPhysicalFile(a.ImageUrl) // картинка варианта ответа
                            });
                        }
                        await _context.SaveChangesAsync();
                    }
                }
            }

            // ---------- 5. ШАБЛОНЫ ДОКУМЕНТОВ ----------
            foreach (var template in source.DocumentTemplates.OrderBy(t => t.SortOrder))
            {
                _context.CourseDocumentTemplates.Add(new CourseDocumentTemplate
                {
                    CourseId = newCourse.Id,
                    Name = template.Name,
                    FilePath = CopyPhysicalFile(template.FilePath),
                    IsPerStudent = template.IsPerStudent,
                    RequiresCommission = template.RequiresCommission,
                    SortOrder = template.SortOrder
                });
            }

            await _auditService.LogAsync("Create", "Course", newCourse.Id, $"Дублирован курс: {source.Title} -> {newCourse.Title}");
            await _context.SaveChangesAsync();

            await tx.CommitAsync();

            return Json(new
            {
                success = true,
                course = new
                {
                    id = newCourse.Id,
                    title = newCourse.Title,
                    description = newCourse.Description ?? "",
                    imageUrl = newCourse.ImageUrl ?? "",
                    createdAt = newCourse.CreatedAt.ToString("dd.MM.yyyy"),
                    materialsCount = materialsCount
                }
            });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return Json(new { success = false, message = $"Ошибка при дублировании: {ex.Message}" });
        }
    }

    // =================================================================================
    // ХЕЛПЕР: физическое копирование одного файла (с сохранением в той же папке)
    // Возвращает НОВЫЙ относительный путь или null, если исходника нет на диске.
    // =================================================================================
    private string? CopyPhysicalFile(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || !relativePath.StartsWith("/uploads/"))
            return relativePath; // не файл — оставляем как есть (для Text-контента и т.п.)

        string sourceFullPath = Path.Combine(_env.WebRootPath, relativePath.TrimStart('/'));
        if (!System.IO.File.Exists(sourceFullPath))
            return null; // физически отсутствует — не будем хранить битую ссылку

        string folder = Path.GetDirectoryName(sourceFullPath)!;
        string extension = Path.GetExtension(sourceFullPath);
        string newFileName = Guid.NewGuid().ToString() + extension;
        string destFullPath = Path.Combine(folder, newFileName);

        System.IO.File.Copy(sourceFullPath, destFullPath, overwrite: false);

        // пересобираем относительный путь: та же папка, новое имя
        string relativeFolder = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? "/uploads";
        return $"{relativeFolder}/{newFileName}";
    }

    // =================================================================================
    // ХЕЛПЕР: копирование содержимого материала в зависимости от типа
    // =================================================================================
    private string? CopyMaterialContent(CourseMaterial material)
    {
        if (string.IsNullOrEmpty(material.ContentOrPath))
            return material.ContentOrPath;

        switch (material.Type)
        {
            case MaterialType.Text:
                // HTML-текст — копируем как есть
                return material.ContentOrPath;

            case MaterialType.Pdf:
            case MaterialType.Video:
            case MaterialType.Image:
                // Одиночный файл
                return CopyPhysicalFile(material.ContentOrPath);

            case MaterialType.Assignment:
                // Формат хранения: "fileUrl|||htmlText"
                {
                    var parts = material.ContentOrPath.Split("|||");
                    string oldFile = parts.Length > 0 ? parts[0] : "";
                    string text = parts.Length > 1 ? parts[1] : "";
                    string newFile = string.IsNullOrEmpty(oldFile)
                        ? ""
                        : (CopyPhysicalFile(oldFile) ?? "");
                    return $"{newFile}|||{text}";
                }

            case MaterialType.Scorm:
                // SCORM — копируем всю распакованную папку целиком
                return CopyScormFolder(material.ContentOrPath);

            case MaterialType.Test:
                // У теста файлов в ContentOrPath нет — вопросы/ответы копируются отдельно
                return material.ContentOrPath;

            default:
                return material.ContentOrPath;
        }
    }

    // =================================================================================
    // ХЕЛПЕР: копирование папки SCORM-курса
    // =================================================================================
    private string? CopyScormFolder(string? contentOrPath)
    {
        // Ожидаемый формат: /uploads/materials/scorm/{folderName}/{entryPoint}
        if (string.IsNullOrEmpty(contentOrPath) || !contentOrPath.StartsWith("/uploads/materials/scorm/"))
            return contentOrPath;

        var parts = contentOrPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // parts = ["uploads","materials","scorm","{folderName}","{entryPoint...}"]
        if (parts.Length < 5) return contentOrPath;

        string oldFolderName = parts[3];
        string entryPoint = string.Join("/", parts.Skip(4));

        string scormRoot = Path.Combine(_env.WebRootPath, "uploads", "materials", "scorm");
        string oldFolder = Path.Combine(scormRoot, oldFolderName);
        if (!Directory.Exists(oldFolder)) return null;

        string newFolderName = Guid.NewGuid().ToString();
        string newFolder = Path.Combine(scormRoot, newFolderName);

        // рекурсивное копирование папки
        CopyDirectoryRecursive(oldFolder, newFolder);

        return $"/uploads/materials/scorm/{newFolderName}/{entryPoint}";
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string relPath = Path.GetRelativePath(sourceDir, file);
            string destFile = Path.Combine(destDir, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            System.IO.File.Copy(file, destFile, overwrite: false);
        }
    }
}

