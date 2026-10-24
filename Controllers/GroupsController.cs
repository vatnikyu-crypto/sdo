using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using ClosedXML.Excel;
using SdoApp.Models;
using System.IO.Compression;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SdoApp.Controllers;

[Authorize(Roles = "Админ")]
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
    [HttpPost]
    public async Task<IActionResult> Create(string groupNumber, string title, DateTime startDate, DateTime endDate, DateTime? materialAccessDate)
    {
        if (string.IsNullOrWhiteSpace(groupNumber) || string.IsNullOrWhiteSpace(title))
        {
            return Json(new { success = false, message = "Номер и наименование группы обязательны для заполнения!" });
        }

        if (endDate < startDate)
        {
            return Json(new { success = false, message = "Дата окончания не может быть раньше даты начала обучения!" });
        }

        if (materialAccessDate.HasValue && materialAccessDate.Value.Date < endDate.Date)
        {
            return Json(new { success = false, message = "Дата доступа к материалам не может быть раньше даты окончания обучения!" });
        }

        try
        {
            var newGroup = new Group
            {
                GroupNumber = groupNumber.Trim(),
                Title = title.Trim(),
                StartDate = startDate.Date,
                EndDate = endDate.Date,
                MaterialAccessDate = endDate.Date // Сохраняем новую дату
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

    // POST: /Groups/UpdateGroupSettings
    // Обновление верхней карточки на странице редактирования
    [HttpPost]
    public async Task<IActionResult> UpdateGroupSettings(int groupId, string groupNumber, string title, DateTime startDate, DateTime endDate, DateTime? materialAccessDate)
    {
        if (string.IsNullOrWhiteSpace(groupNumber) || string.IsNullOrWhiteSpace(title))
        {
            return Json(new { success = false, message = "Номер и наименование группы обязательны!" });
        }

        if (endDate < startDate)
        {
            return Json(new { success = false, message = "Дата окончания не может быть раньше даты начала обучения!" });
        }

        if (materialAccessDate.HasValue && materialAccessDate.Value.Date < endDate.Date)
        {
            return Json(new { success = false, message = "Дата доступа к материалам не может быть раньше даты окончания обучения!" });
        }

        try
        {
            var group = await _context.Groups.FindAsync(groupId);
            if (group == null) return Json(new { success = false, message = "Группа не найдена!" });

            group.GroupNumber = groupNumber.Trim();
            group.Title = title.Trim();
            group.StartDate = startDate.Date;
            group.EndDate = endDate.Date;
            group.MaterialAccessDate = materialAccessDate?.Date;

            _context.Groups.Update(group);
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Ошибка при обновлении: {ex.Message}" });
        }
    }

    // POST: /Groups/Delete/5
    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var group = await _context.Groups
            .Include(g => g.GroupStudents)
            .FirstOrDefaultAsync(g => g.Id == id);

        if (group == null) return Json(new { success = false, message = "Группа не найдена!" });

        try
        {
            // НОВОЕ: Получаем список студентов и курсов, привязанных к этой группе
            var studentIds = group.GroupStudents?.Select(gs => gs.StudentId).ToList() ?? new List<int>();
            var courseIds = await _context.GroupCourseConfigs
                .Where(gc => gc.GroupId == id)
                .Select(gc => gc.CourseId)
                .ToListAsync();

            // НОВОЕ: Удаляем прогресс (вместе с номерами удостоверений) для этих студентов и курсов
            if (studentIds.Any() && courseIds.Any())
            {
                var progressToRemove = await _context.CourseProgresses
                    .Where(p => studentIds.Contains(p.StudentId) &&
                                courseIds.Contains(p.CourseId) &&
                                p.GroupId == id)  // ВАЖНО: только для этой группы
                    .ToListAsync();

                _context.CourseProgresses.RemoveRange(progressToRemove);
            }

            // Удаляем саму группу (связи удалятся каскадно)
            _context.Groups.Remove(group);
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Ошибка при удалении: {ex.Message}" });
        }
    }

    [HttpPost]
    public async Task<IActionResult> DeleteSelected([FromBody] List<int> ids)
    {
        if (ids == null || !ids.Any())
            return Json(new { success = false, message = "Не выбрано ни одной группы для удаления!" });

        try
        {
            var groupsToDelete = await _context.Groups
                .Include(g => g.GroupStudents)
                .Where(g => ids.Contains(g.Id))
                .ToListAsync();

            // Удаляем прогресс ТОЛЬКО для этих групп
            foreach (var grp in groupsToDelete)
            {
                var grpStudentIds = grp.GroupStudents?.Select(gs => gs.StudentId).ToList() ?? new List<int>();
                var grpCourseIds = await _context.GroupCourseConfigs
                    .Where(gc => gc.GroupId == grp.Id)
                    .Select(gc => gc.CourseId)
                    .ToListAsync();

                if (grpStudentIds.Any() && grpCourseIds.Any())
                {
                    var progressToRemove = await _context.CourseProgresses
                        .Where(p => grpStudentIds.Contains(p.StudentId) &&
                                    grpCourseIds.Contains(p.CourseId) &&
                                    p.GroupId == grp.Id)
                        .ToListAsync();

                    _context.CourseProgresses.RemoveRange(progressToRemove);
                }
            }

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

        // НОВОЕ: Передаем даты для верхней карточки настроек
        ViewBag.StartDate = group.StartDate.ToString("yyyy-MM-dd");
        ViewBag.EndDate = group.EndDate.ToString("yyyy-MM-dd");
        ViewBag.MaterialAccessDate = group.MaterialAccessDate?.ToString("yyyy-MM-dd");

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

        var allowedCompanyIds = await _context.GroupCompanies
            .Where(gc => gc.GroupId == groupId)
            .Select(gc => gc.CompanyId)
            .ToListAsync();

        var allStudents = await _context.Users
            .Include(u => u.Company)
            .Where(u => u.Role == UserRole.Обучающийся)
            .ToListAsync();

        var filteredStudents = allStudents
            .Where(u => (u.LastName != null && u.LastName.ToLower().Contains(lowerTerm)) ||
                        (u.FirstName != null && u.FirstName.ToLower().Contains(lowerTerm)))
            .Where(u =>
            {
                if (u.CompanyId == null) return true;
                if (!allowedCompanyIds.Any()) return false;
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

        var allSystemCompanies = await _context.Companies
            .OrderBy(c => c.Name)
            .ToListAsync();

        var safeCompaniesStore = allSystemCompanies.Select(c => new
        {
            id = c.Id,
            name = c.Name ?? "",
            inn = c.Inn ?? ""
        }).ToList();

        return Json(new
        {
            students = studentsInGroup,
            courses = coursesInGroup,
            companies = companiesInGroup,
            allStudentsStore = safeStudentsStore,
            allCompaniesStore = safeCompaniesStore
        });
    }

    // POST: /Groups/SaveGroupSkeleton
    [HttpPost]
    public async Task<IActionResult> SaveGroupSkeleton(int groupId, List<int> studentIds, List<int> courseIds, List<int> companyIds)
    {
        try
        {
            var group = await _context.Groups.FindAsync(groupId);
            if (group == null) return Json(new { success = false, message = "Группа не найдена!" });

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
                        _context.GroupCourseConfigs.Add(new GroupCourseConfig
                        {
                            GroupId = groupId,
                            CourseId = cId,
                            AllowedStudentIds = "",
                            ProtocolNumber = group.GroupNumber,
                            CourseStartDate = group.StartDate,
                            CourseEndDate = group.EndDate
                        });
                    }
                }
            }

            // 3. Обновляем привязанные компании группы
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

            // 4. АВТОМАТИЧЕСКОЕ ПРИСВОЕНИЕ НОМЕРОВ МИНТРУДА (просто числа: 1, 2, 3...)
            var actualStudentIds = await _context.GroupStudents
                .Where(gs => gs.GroupId == groupId)
                .Select(gs => gs.StudentId)
                .ToListAsync();

            var mintrudConfigs = await _context.GroupCourseConfigs
                .Where(gc => gc.GroupId == groupId)
                .Join(_context.Courses.Where(c => c.MintrudProgramId != null),
                      gc => gc.CourseId,
                      c => c.Id,
                      (gc, c) => gc)
                .ToListAsync();

            if (mintrudConfigs.Any() && actualStudentIds.Any())
            {
                // Ищем максимальный существующий номер Минтруда (просто число)
                var existingMtNumbers = await _context.CourseProgresses
                    .Where(p => p.MintrudNumber != null)
                    .Select(p => p.MintrudNumber)
                    .ToListAsync();

                int maxMt = 0;
                foreach (var mt in existingMtNumbers)
                {
                    if (int.TryParse(mt, out int n) && n > maxMt)
                    {
                        maxMt = n;
                    }
                }

                int nextMt = maxMt + 1;
                bool anyChanged = false;

                foreach (var cfg in mintrudConfigs)
                {
                    foreach (var sId in actualStudentIds)
                    {
                        var progress = await _context.CourseProgresses
                            .FirstOrDefaultAsync(p => p.StudentId == sId &&
                                                      p.CourseId == cfg.CourseId &&
                                                      p.GroupId == groupId);

                        if (progress == null)
                        {
                            progress = new CourseProgress
                            {
                                StudentId = sId,
                                CourseId = cfg.CourseId,
                                GroupId = groupId
                            };
                            _context.CourseProgresses.Add(progress);
                        }

                        // Присваиваем ТОЛЬКО если номера еще нет
                        if (string.IsNullOrEmpty(progress.MintrudNumber))
                        {
                            progress.MintrudNumber = nextMt.ToString(); // Просто "1", "2", "3"
                            nextMt++;
                            anyChanged = true;
                        }
                    }
                }

                if (anyChanged)
                {
                    await _context.SaveChangesAsync();
                }
            }

            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Ошибка сохранения костяка: {ex.Message}" });
        }
    }

    // POST: /Groups/SaveCourseConfig
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

        var companies = await _context.Companies.ToListAsync();

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

        var students = await _context.GroupStudents
            .Where(gs => gs.GroupId == id)
            .Include(gs => gs.Student)
            .ThenInclude(s => s!.Company)
            .Select(gs => gs.Student)
            .Where(s => s != null)
            .ToListAsync();

        if (students == null || !students.Any())
            return NotFound("В группе нет слушателей для выгрузки.");

        var studentsByCompany = students
            .GroupBy(s => s?.Company != null ? s.Company.Name : "Частное лицо")
            .ToList();

        string SanitizeFileName(string name) => System.Text.RegularExpressions.Regex.Replace(name, @"[\\/:*?""<>|]", "").Trim();

        // ============================================================
        // СЛУЧАЙ 1: Все студенты из одной компании — отдаем один Excel
        // ============================================================
        if (studentsByCompany.Count == 1)
        {
            var singleGroup = studentsByCompany.First();
            string companyName = SanitizeFileName(singleGroup.Key);

            byte[] fileBytes = GenerateExcelForCompany(singleGroup.Key, singleGroup.ToList());
            string fileName = $"Доступы для группы № {group.GroupNumber} - {companyName}.xlsx";

            // Правильное кодирование русского имени (RFC 5987)
            var contentDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
            {
                FileNameStar = fileName
            };
            Response.Headers[Microsoft.Net.Http.Headers.HeaderNames.ContentDisposition] = contentDisposition.ToString();

            return File(fileBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        }

        // ============================================================
        // СЛУЧАЙ 2: Несколько компаний — пакуем в ZIP-архив
        // ============================================================
        using (var memoryStream = new MemoryStream())
        {
            using (var zipArchive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
            {
                foreach (var companyGroup in studentsByCompany)
                {
                    string companyName = SanitizeFileName(companyGroup.Key);
                    byte[] excelBytes = GenerateExcelForCompany(companyGroup.Key, companyGroup.ToList());
                    string entryName = $"Доступы для группы № {group.GroupNumber} - {companyName}.xlsx";

                    var zipEntry = zipArchive.CreateEntry(entryName, CompressionLevel.Optimal);
                    using (var entryStream = zipEntry.Open())
                    {
                        entryStream.Write(excelBytes, 0, excelBytes.Length);
                    }
                }
            }

            string zipFileName = $"Доступы для группы № {group.GroupNumber} (по компаниям).zip";

            // Правильное кодирование русского имени ZIP-файла (RFC 5987)
            var contentDispositionZip = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
            {
                FileNameStar = zipFileName
            };
            Response.Headers[Microsoft.Net.Http.Headers.HeaderNames.ContentDisposition] = contentDispositionZip.ToString();

            return File(memoryStream.ToArray(), "application/zip");
        }
    }

    private byte[] GenerateExcelForCompany(string companyName, List<User?> students)
    {
        using (var workbook = new XLWorkbook())
        {
            var worksheet = workbook.Worksheets.Add("Доступы");

            worksheet.Cell(1, 1).Value = "Данные для входа в систему https://sdo.dpo-energo.ru для слушателей организации";
            worksheet.Range(1, 1, 1, 8).Merge();
            worksheet.Cell(1, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            worksheet.Cell(1, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            worksheet.Cell(1, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF2CC");

            worksheet.Cell(2, 1).Value = companyName;
            worksheet.Range(2, 1, 2, 8).Merge();
            worksheet.Cell(2, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            worksheet.Cell(2, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF2CC");

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

            int row = 4;
            foreach (var user in students)
            {
                if (user == null) continue;

                string escapedLogin = Uri.EscapeDataString(user.Login ?? "");
                string escapedPassword = Uri.EscapeDataString(user.Password ?? "");

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