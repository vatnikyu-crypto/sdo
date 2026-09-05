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
    public IActionResult Create()
    {
        return View();
    }

    // POST: /Courses/Create
    // Обработка формы и загрузка картинок на сервер
    [HttpPost]
    public async Task<IActionResult> Create(string title, string? description, IFormFile? imageFile)
    {
        ViewBag.Title = title;
        ViewBag.Description = description;

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
            savedImageUrl = "/uploads/courses/default-course.jpg";
        }

        var newCourse = new Course
        {
            Title = title.Trim(),
            Description = description?.Trim(),
            ImageUrl = savedImageUrl,
            CreatedAt = DateTime.UtcNow
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


    // GET: /Courses/Edit/5
    // Открывает страницу редактирования с заполненными данными курса
    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var course = await _context.Courses.FindAsync(id);
        if (course == null) return NotFound();

        ViewBag.Id = course.Id;
        ViewBag.CourseTitle = course.Title;
        ViewBag.Description = course.Description;
        ViewBag.ImageUrl = course.ImageUrl;

        return View();
    }

    // POST: /Courses/Edit/5
    // Сохраняет изменения, проверяет файлы и удаляет старую обложку при замене
    [HttpPost]
    public async Task<IActionResult> Edit(int id, string title, string? description, IFormFile? imageFile)
    {
        var course = await _context.Courses.FindAsync(id);
        if (course == null) return NotFound();

        // Буферизируем данные на случай ошибки
        ViewBag.Id = course.Id;
        ViewBag.CourseTitle = title;
        ViewBag.Description = description;
        ViewBag.ImageUrl = course.ImageUrl;

        if (string.IsNullOrWhiteSpace(title))
        {
            ModelState.AddModelError("", "Ошибка: Название курса является обязательным полем!");
            return View();
        }

        // Если админ загрузил новую картинку
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
                string uploadsFolder = Path.Combine(_env.WebRootPath, "uploads", "courses");
                if (!Directory.Exists(uploadsFolder))
                {
                    Directory.CreateDirectory(uploadsFolder);
                }

                // Генерируем новое уникальное имя
                string uniqueFileName = Guid.NewGuid().ToString() + extension;
                string fileSavePath = Path.Combine(uploadsFolder, uniqueFileName);

                using (var fileStream = new FileStream(fileSavePath, FileMode.Create))
                {
                    await imageFile.CopyToAsync(fileStream);
                }

                // УДАЛЕНИЕ СТАРOЙ КАРТИНКИ: если она была и она не дефолтная
                if (!string.IsNullOrEmpty(course.ImageUrl) && !course.ImageUrl.Contains("default-course.jpeg"))
                {
                    string oldImagePath = Path.Combine(_env.WebRootPath, course.ImageUrl.TrimStart('/'));
                    if (System.IO.File.Exists(oldImagePath))
                    {
                        System.IO.File.Delete(oldImagePath);
                    }
                }

                // Записываем новый путь в модель
                course.ImageUrl = $"/uploads/courses/{uniqueFileName}";
            }
            catch (Exception ex)
            {
                ModelState.AddModelError("", $"Ошибка при обновлении файла: {ex.Message}");
                return View();
            }
        }

        // Обновляем текстовые поля
        course.Title = title.Trim();
        course.Description = description?.Trim();

        _context.Courses.Update(course);
        await _context.SaveChangesAsync();

        return RedirectToAction(nameof(Index));
    }

}

