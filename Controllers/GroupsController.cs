using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SdoApp.Controllers;

[Authorize(Roles = "Админ")] // Доступ только для Администратора
public class GroupsController : Controller
{
    private readonly AppDbContext _context;

    public GroupsController(AppDbContext context)
    {
        _context = context;
    }

    // GET: /Groups/Index
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var groups = await _context.Groups
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync();
        return View(groups);
    }

    // POST: /Groups/Create
    // Асинхронное создание группы из модального окна
    [HttpPost]
    public async Task<IActionResult> Create(string groupNumber, string title, DateTime startDate, DateTime endDate)
    {
        if (string.IsNullOrWhiteSpace(groupNumber) || string.IsNullOrWhiteSpace(title))
        {
            return Json(new { success = false, message = "Номер и наименование группы обязательны для заполнения!" });
        }

        if (endDate < startDate)
        {
            return Json(new { success = false, message = "Дата окончания не может быть раньше даты начала обучения!" });
        }

        try
        {
            var newGroup = new Group
            {
                GroupNumber = groupNumber.Trim(),
                Title = title.Trim(),
                StartDate = startDate.Date, // Сохраняем строго чистую дату без времени
                EndDate = endDate.Date
            };

            _context.Groups.Add(newGroup);
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Ошибка БД: {ex.Message}" });
        }
    }

    // POST: /Groups/Delete/5
    // Одиночное асинхронное AJAX-удаление
    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var group = await _context.Groups.FindAsync(id);
        if (group == null) return Json(new { success = false, message = "Группа не найдена!" });

        try
        {
            _context.Groups.Remove(group);
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Ошибка при удалении: {ex.Message}" });
        }
    }

    // POST: /Groups/DeleteSelected
    // МАССОВОЕ асинхронное AJAX-удаление по списку выбранных ID
    [HttpPost]
    public async Task<IActionResult> DeleteSelected([FromBody] List<int> ids)
    {
        if (ids == null || !ids.Any())
            return Json(new { success = false, message = "Не выбрано ни одной группы для удаления!" });

        try
        {
            var groupsToDelete = await _context.Groups.Where(g => ids.Contains(g.Id)).ToListAsync();
            _context.Groups.RemoveRange(groupsToDelete);
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Ошибка массового удаления: {ex.Message}" });
        }
    }

    // GET: /Groups/Edit/5
    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var group = await _context.Groups.FindAsync(id);
        if (group == null) return NotFound();

        ViewBag.GroupId = group.Id;
        ViewBag.GroupNumber = group.GroupNumber;
        ViewBag.GroupTitle = group.Title;

        // ДОБАВЛЕНО: Передаем список всех существующих курсов для выпадающего меню селекта
        ViewBag.AllAvailableCourses = await _context.Courses.OrderBy(c => c.Title).ToListAsync();

        return View();
    }

    // GET: /Groups/SearchStudents?term=Ива
    // API-метод для живого поиска студентов "на лету" без выгрузки всей базы
    [HttpGet]
    public async Task<IActionResult> SearchStudents(string term)
    {
        if (string.IsNullOrWhiteSpace(term)) return Json(new List<object>());

        string lowerTerm = term.ToLower();

        var students = await _context.Users
    .Include(u => u.Company)
    .Where(u => u.Role == UserRole.Обучающийся) // Проверяем строго enum роль
    .ToListAsync(); // Вытаскиваем в память для мягкого поиска без капризов SQLite

        // Фильтруем в памяти, чтобы ТоLower() работал на 100% с русскими буквами
        var filteredStudents = students
            .Where(u => u.LastName.ToLower().Contains(lowerTerm) ||
                       u.FirstName.ToLower().Contains(lowerTerm))
            .Take(10)
            .Select(u => new
            {
                id = u.Id,
                fio = $"{u.LastName} {u.FirstName} {u.MiddleName}".Trim(),
                company = u.Company != null ? u.Company.Name : "Частное лицо",
                position = u.Position ?? "Должность не указана"
            })
            .ToList();

        return Json(filteredStudents);
    }

    // GET: /Groups/GetGroupData/5
    // Отдает текущий состав группы + вообще всех студентов системы для живого поиска на клиенте
    [HttpGet]
    public async Task<IActionResult> GetGroupData(int id)
    {
        // 1. Текущие студенты этой группы
        var studentsInGroup = await _context.GroupStudents
            .Include(s => s.Student)
                .ThenInclude(st => st!.Company)
            .Where(s => s.GroupId == id)
            .Select(s => new
            {
                id = s.StudentId,
                fio = $"{s.Student!.LastName} {s.Student.FirstName} {s.Student.MiddleName}".Trim(),
                company = s.Student.Company != null ? s.Student.Company.Name : "Частное лицо",
                position = s.Student.Position ?? "—"
            })
            .ToListAsync();

        // 2. Текущие курсы этой группы
        var coursesInGroup = await _context.GroupCourseConfigs
            .Include(c => c.Course)
            .Where(c => c.GroupId == id)
            .Select(c => new
            {
                configId = c.Id,
                courseId = c.CourseId,
                title = c.Course!.Title,
                protocol = c.ProtocolNumber,
                start = c.CourseStartDate.HasValue ? c.CourseStartDate.Value.ToString("yyyy-MM-dd") : "",
                end = c.CourseEndDate.HasValue ? c.CourseEndDate.Value.ToString("yyyy-MM-dd") : "",
                allowedIds = c.AllowedStudentIds
            })
            .ToListAsync();

        // 3. ИСПРАВЛЕННЫЙ И БЕЗОПАСНЫЙ ВАРИАНТ ДЛЯ ЛЮБОГО НАПОЛНЕНИЯ БАЗЫ:
        var allSystemStudents = await _context.Users
            .Include(u => u.Company)
            .Where(u => u.Role == UserRole.Обучающийся)
            .ToListAsync(); // Сначала выгружаем плоский список

        // Спокойно и безопасно клеим ФИО в памяти, защищаясь от null-полей
        var safeStudentsStore = allSystemStudents.Select(u => new
        {
            id = u.Id,
            fio = $"{u.LastName ?? ""} {u.FirstName ?? ""} {u.MiddleName ?? ""}".Trim(),
            company = u.Company != null ? u.Company.Name : "Частное лицо",
            position = u.Position ?? "—"
        }).ToList();

        return Json(new
        {
            students = studentsInGroup,
            courses = coursesInGroup,
            allStudentsStore = safeStudentsStore // Отдаем гарантированно заполненный массив!
        });
    }




    // POST: /Groups/SaveGroupSkeleton
    // Сохраняет базовый состав группы (людей и выбранные курсы)
    [HttpPost]
    public async Task<IActionResult> SaveGroupSkeleton(int groupId, List<int> studentIds, List<int> courseIds)
    {
        try
        {
            // 1. Обновляем список студентов в группе
            var oldStudents = await _context.GroupStudents.Where(s => s.GroupId == groupId).ToListAsync();
            _context.GroupStudents.RemoveRange(oldStudents);

            if (studentIds != null)
            {
                foreach (var sId in studentIds)
                {
                    _context.GroupStudents.Add(new GroupStudent { GroupId = groupId, StudentId = sId });
                }
            }

            // 2. Обновляем список курсов в группе (не затирая старые протоколы и даты, если курс уже был привязан)
            var currentConfigs = await _context.GroupCourseConfigs.Where(c => c.GroupId == groupId).ToListAsync();

            // Удаляем те конфигурации, которые админ убрал из списка
            var configsToRemove = currentConfigs.Where(cc => courseIds == null || !courseIds.Contains(cc.CourseId)).ToList();
            _context.GroupCourseConfigs.RemoveRange(configsToRemove);

            // Добавляем новые конфигурации для свежепривязанных курсов
            if (courseIds != null)
            {
                foreach (var cId in courseIds)
                {
                    if (!currentConfigs.Any(cc => cc.CourseId == cId))
                    {
                        _context.GroupCourseConfigs.Add(new GroupCourseConfig
                        {
                            GroupId = groupId,
                            CourseId = cId,
                            AllowedStudentIds = "" // По умолчанию пусто (будут допущены все при тонкой настройке)
                        });
                    }
                }
            }

            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Ошибка сохранения костяка: {ex.Message}" });
        }
    }

    // POST: /Groups/SaveCourseConfig
    // Тонкая настройка конкретного курса: сохранение протокола, дат и строки с ID студентов
    [HttpPost]
    public async Task<IActionResult> SaveCourseConfig(int configId, string protocolNumber, DateTime? startDate, DateTime? endDate, List<int> allowedStudentIds)
    {
        try
        {
            var config = await _context.GroupCourseConfigs.FindAsync(configId);
            if (config == null) return Json(new { success = false, message = "Конфигурация курса не найдена!" });

            config.ProtocolNumber = protocolNumber?.Trim() ?? string.Empty;
            config.CourseStartDate = startDate;
            config.CourseEndDate = endDate;

            // Пакуем массив ID студентов в строку через запятую (например: "1,4,15")
            config.AllowedStudentIds = allowedStudentIds != null && allowedStudentIds.Any()
                ? string.Join(",", allowedStudentIds)
                : string.Empty;

            _context.GroupCourseConfigs.Update(config);
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Ошибка сохранения курса: {ex.Message}" });
        }
    }
}
