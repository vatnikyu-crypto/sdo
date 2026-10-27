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

[Authorize(Roles = "Админ")] // Доступ только для Администратора
public class CoursesController : Controller
{
    private readonly AppDbContext _context;
    private readonly IWebHostEnvironment _env;

    public CoursesController(AppDbContext context, IWebHostEnvironment env)
    {
        _context = context;
        _env = env;
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

            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Ошибка при полном каскадном удалении курса: {ex.Message}" });
        }
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
}

