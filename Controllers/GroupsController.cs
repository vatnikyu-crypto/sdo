using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using ClosedXML.Excel;
using SdoApp.Models;
using System.IO.Compression; // Обязательно добавь для работы с ZIP
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
            .Include(g => g.GroupCompanies)
            .ThenInclude(gc => gc.Company)
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

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var group = await _context.Groups.FindAsync(id);
        if (group == null) return NotFound();

        ViewBag.GroupId = group.Id;
        ViewBag.GroupNumber = group.GroupNumber;
        ViewBag.GroupTitle = group.Title;

        // Передаем список всех существующих курсов для выпадающего меню селекта
        ViewBag.AllAvailableCourses = await _context.Courses.OrderBy(c => c.Title).ToListAsync();

        // 1. ВЫТАСКИВАЕМ ВСЕ КОМПАНИИ: Чтобы админ мог выбрать их на странице
        ViewBag.AllAvailableCompanies = await _context.Companies
            .OrderBy(c => c.Name)
            .ToListAsync();

        // 2. ВЫТАСКИВАЕМ ТЕКУЩИЕ СВЯЗИ: ID компаний, которые уже закреплены за этой группой
        ViewBag.CurrentCompanyIds = await _context.GroupCompanies
            .Where(gc => gc.GroupId == id)
            .Select(gc => gc.CompanyId)
            .ToListAsync();

        return View();
    }

    // GET: /Groups/SearchStudents?term=Ива&groupId=5
    [HttpGet]
    public async Task<IActionResult> SearchStudents(string term, int groupId)
    {
        if (string.IsNullOrWhiteSpace(term)) return Json(new List<object>());

        string lowerTerm = term.ToLower().Trim();

        // 1. Получаем ID компаний, которые уже сохранены в этой группе
        var allowedCompanyIds = await _context.GroupCompanies
            .Where(gc => gc.GroupId == groupId)
            .Select(gc => gc.CompanyId)
            .ToListAsync();

        // 2. Вытаскиваем всех студентов в память (как в твоем изначальном коде)
        var allStudents = await _context.Users
            .Include(u => u.Company)
            .Where(u => u.Role == UserRole.Обучающийся)
            .ToListAsync();

        // 3. Фильтруем в памяти
        var filteredStudents = allStudents
            // Ищем по фамилии или имени
            .Where(u => (u.LastName != null && u.LastName.ToLower().Contains(lowerTerm)) ||
                        (u.FirstName != null && u.FirstName.ToLower().Contains(lowerTerm)))
            // Фильтруем по компаниям
            .Where(u =>
            {
                // УСЛОВИЕ 1: Если у пользователя вообще нет компании (CompanyId == null) — это частное лицо, показываем.
                if (u.CompanyId == null) return true;

                // УСЛОВИЕ 2: Если к группе еще не привязана ни одна компания, скрываем корпоративных сотрудников
                if (!allowedCompanyIds.Any()) return false;

                // УСЛОВИЕ 3: Показываем, только если компания пользователя есть в списке группы
                return allowedCompanyIds.Contains(u.CompanyId.Value);
            })
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
    [HttpGet]
    public async Task<IActionResult> GetGroupData(int id)
    {
        // 1. Текущие студенты этой группы (Твой оригинал)
        var studentsInGroup = await _context.GroupStudents
            .Include(s => s.Student)
            .ThenInclude(st => st!.Company)
            .Where(s => s.GroupId == id)
            .Select(s => new
            {
                id = s.StudentId,
                fio = $"{s.Student!.LastName ?? ""} {s.Student.FirstName ?? ""} {s.Student.MiddleName ?? ""}".Trim(),
                company = s.Student.Company != null ? s.Student.Company.Name : "Частное лицо",
                position = s.Student.Position ?? "—"
            })
            .ToListAsync();

        // 2. Текущие курсы этой группы (Твой оригинал)
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

        // 3. Текущие привязанные компании этой группы (Твой оригинал)
        var companiesInGroup = await _context.GroupCompanies
            .Include(gc => gc.Company)
            .Where(gc => gc.GroupId == id)
            .Select(gc => new
            {
                id = gc.CompanyId,
                name = gc.Company!.Name,
                inn = gc.Company.Inn
            })
            .ToListAsync();

        // 4. Твой оригинальный склад ВСЕХ студентов для сверхбыстрого поиска на клиенте
        var allSystemStudents = await _context.Users
            .Include(u => u.Company)
            .Where(u => u.Role == UserRole.Обучающийся)
            .ToListAsync();

        var safeStudentsStore = allSystemStudents.Select(u => new
        {
            id = u.Id,
            fio = $"{u.LastName ?? ""} {u.FirstName ?? ""} {u.MiddleName ?? ""}".Trim(),
            company = u.Company != null ? u.Company.Name : "Частное лицо",
            position = u.Position ?? "—"
        }).ToList();

        // =================================================================
        // 5. ИСПРАВЛЕНИЕ: Выгружаем ВСЕ компании системы для живого поиска на клиенте
        // =================================================================
        var allSystemCompanies = await _context.Companies
            .OrderBy(c => c.Name)
            .ToListAsync();

        var safeCompaniesStore = allSystemCompanies.Select(c => new
        {
            id = c.Id,
            name = c.Name ?? "",
            inn = c.Inn ?? ""
        }).ToList();

        // Возвращаем объект со ВСЕМИ нужными складами данных
        return Json(new
        {
            students = studentsInGroup,
            courses = coursesInGroup,
            companies = companiesInGroup,
            allStudentsStore = safeStudentsStore,
            allCompaniesStore = safeCompaniesStore // <-- ТЕПЕРЬ ДАННЫЕ СЮДА КЛАДУТСЯ!
        });
    }

    // POST: /Groups/SaveGroupSkeleton
    [HttpPost]
    public async Task<IActionResult> SaveGroupSkeleton(int groupId, List<int> studentIds, List<int> courseIds, List<int> companyIds)
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

            // 2. Обновляем список курсов в группе
            var currentConfigs = await _context.GroupCourseConfigs.Where(c => c.GroupId == groupId).ToListAsync();
            var configsToRemove = currentConfigs.Where(cc => courseIds == null || !courseIds.Contains(cc.CourseId)).ToList();
            _context.GroupCourseConfigs.RemoveRange(configsToRemove);
            if (courseIds != null)
            {
                foreach (var cId in courseIds)
                {
                    if (!currentConfigs.Any(cc => cc.CourseId == cId))
                    {
                        _context.GroupCourseConfigs.Add(new GroupCourseConfig { GroupId = groupId, CourseId = cId, AllowedStudentIds = "" });
                    }
                }
            }

            // 3. НОВОЕ: Обновляем привязанные компании группы
            var oldCompanies = await _context.GroupCompanies.Where(gc => gc.GroupId == groupId).ToListAsync();
            _context.GroupCompanies.RemoveRange(oldCompanies);
            if (companyIds != null)
            {
                foreach (var cId in companyIds)
                {
                    _context.GroupCompanies.Add(new GroupCompany { GroupId = groupId, CompanyId = cId });
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

    // GET: /Groups/SearchCompanies?term=Техно
    [HttpGet]
    public async Task<IActionResult> SearchCompanies(string term)
    {
        if (string.IsNullOrWhiteSpace(term)) return Json(new List<object>());

        string lowerTerm = term.ToLower();

        // 1. Вытаскиваем все компании в память (как и студентов), чтобы защитить кириллицу от капризов SQLite
        var companies = await _context.Companies
            .ToListAsync();

        // 2. Мягко фильтруем в памяти, проверяя совпадение по названию или ИНН
        var filteredCompanies = companies
            .Where(c => (c.Name != null && c.Name.ToLower().Contains(lowerTerm)) ||
                        (c.Inn != null && c.Inn.Contains(lowerTerm)))
            .Take(10)
            .Select(c => new
            {
                id = c.Id,
                name = c.Name,
                inn = c.Inn
            })
            .ToList();

        return Json(filteredCompanies);
    }

    // GET: /Groups/DownloadAccesses/5
    [HttpGet]
    public async Task<IActionResult> DownloadAccesses(int id)
    {
        var group = await _context.Groups.FindAsync(id);
        if (group == null) return NotFound("Группа не найдена");

        // 2. Вытаскиваем всех студентов этой группы
        var students = await _context.GroupStudents
            .Where(gs => gs.GroupId == id)
            .Include(gs => gs.Student)
            .ThenInclude(s => s!.Company)
            .Select(gs => gs.Student)
            .Where(s => s != null)
            .ToListAsync();

        if (students == null || !students.Any())
            return NotFound("В группе нет слушателей для выгрузки.");

        // 3. Группируем студентов по названию компании
        var studentsByCompany = students
            .GroupBy(s => s.Company != null ? s.Company.Name : "Частное лицо")
            .ToList();

        // Функция для безопасного имени файла (убирает \, /, :, *, ?, ", <, >, |)
        string SanitizeFileName(string name) => System.Text.RegularExpressions.Regex.Replace(name, @"[\\/:*?""<>|]", "").Trim();

        // 4. Если компания только одна (или все физлица) — отдаем один Excel
        if (studentsByCompany.Count == 1)
        {
            var singleGroup = studentsByCompany.First();
            string companyName = SanitizeFileName(singleGroup.Key);

            byte[] fileBytes = GenerateExcelForCompany(singleGroup.Key, singleGroup.ToList());
            string fileName = $"Доступы для группы № {group.GroupNumber} - {companyName}.xlsx";

            return File(fileBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        // 5. Если компаний несколько — создаем ZIP-архив
        using (var memoryStream = new MemoryStream())
        {
            using (var zipArchive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
            {
                foreach (var companyGroup in studentsByCompany)
                {
                    string companyName = SanitizeFileName(companyGroup.Key);

                    // Генерируем Excel для конкретной компании
                    byte[] excelBytes = GenerateExcelForCompany(companyGroup.Key, companyGroup.ToList());

                    // Имя файла внутри архива
                    string entryName = $"Доступы для группы № {group.GroupNumber} - {companyName}.xlsx";

                    // Создаем файл в архиве и записываем туда байты
                    var zipEntry = zipArchive.CreateEntry(entryName, CompressionLevel.Optimal);
                    using (var entryStream = zipEntry.Open())
                    {
                        entryStream.Write(excelBytes, 0, excelBytes.Length);
                    }
                }
            }

            string zipFileName = $"Доступы для группы № {group.GroupNumber} (по компаниям).zip";
            return File(memoryStream.ToArray(), "application/zip", zipFileName);
        }
    }

    private byte[] GenerateExcelForCompany(string companyName, List<User> students)
    {
        using (var workbook = new XLWorkbook())
        {
            var worksheet = workbook.Worksheets.Add("Доступы");

            // --- ФОРМИРУЕМ ШАПКУ ---
            // Строка 1
            worksheet.Cell(1, 1).Value = "Данные для входа в систему https://sdo.dpo-energo.ru для слушателей организации";
            worksheet.Range(1, 1, 1, 8).Merge();
            worksheet.Cell(1, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            worksheet.Cell(1, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            worksheet.Cell(1, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF2CC");

            // Строка 2 (Подставляем правильное название компании)
            worksheet.Cell(2, 1).Value = companyName;
            worksheet.Range(2, 1, 2, 8).Merge();
            worksheet.Cell(2, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            worksheet.Cell(2, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF2CC");

            // Строка 3 (Заголовки)
            string[] headers = { "Фамилия", "Имя", "Отчество", "Логин", "Пароль", "Отдел", "Должность", "Ссылка для авторизации" };
            for (int i = 0; i < headers.Length; i++)
            {
                var cell = worksheet.Cell(3, i + 1);
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F2F2F2");
            }

            // --- ЗАПОЛНЯЕМ ДАННЫЕ ---
            int row = 4;
            foreach (var user in students)
            {
                if (user == null) continue;

                string escapedLogin = Uri.EscapeDataString(user.Login ?? "");
                string escapedPassword = Uri.EscapeDataString(user.Password ?? "");

                // Ссылка авторизации
                string authUrl = $"http://localhost:5018/Auth/Login?login={escapedLogin}&password={escapedPassword}";

                worksheet.Cell(row, 1).Value = user.LastName ?? "";
                worksheet.Cell(row, 2).Value = user.FirstName ?? "";
                worksheet.Cell(row, 3).Value = user.MiddleName ?? "";
                worksheet.Cell(row, 4).Value = user.Login ?? "";
                worksheet.Cell(row, 5).Value = user.Password ?? "";
                worksheet.Cell(row, 6).Value = user.Department ?? "—";
                worksheet.Cell(row, 7).Value = user.Position ?? "—";

                var linkCell = worksheet.Cell(row, 8);
                linkCell.Value = authUrl;
                linkCell.SetHyperlink(new XLHyperlink(authUrl));
                linkCell.Style.Font.FontColor = XLColor.Blue;
                linkCell.Style.Font.Underline = XLFontUnderlineValues.Single;

                // Рамки
                worksheet.Range(row, 1, row, 8).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                worksheet.Range(row, 1, row, 8).Style.Border.InsideBorder = XLBorderStyleValues.Thin;

                row++;
            }

            worksheet.Columns().AdjustToContents();

            using (var stream = new MemoryStream())
            {
                workbook.SaveAs(stream);
                return stream.ToArray();
            }
        }
    }
}
