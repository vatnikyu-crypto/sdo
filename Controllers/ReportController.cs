using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models;
using SdoApp.Models.DTO;
using MiniSoftware;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using ClosedXML.Excel;

using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

// --- АЛИАСЫ ДЛЯ XCEED (чтобы не конфликтовали с OpenXml) ---
using XceedDocX = Xceed.Words.NET.DocX;
using XceedAlignment = Xceed.Document.NET.Alignment;
using XceedTableDesign = Xceed.Document.NET.TableDesign;
using XceedFont = Xceed.Document.NET.Font;
// -----------------------------------------------------------

namespace SdoApp.Controllers;

[Authorize(Roles = "Админ")]
public class ReportController : Controller
{
    private readonly AppDbContext _context;
    private readonly IWebHostEnvironment _env;

    public ReportController(AppDbContext context, IWebHostEnvironment env)
    {
        _context = context;
        _env = env;
    }

    // 1. Главная страница: список групп -> курсы -> студенты
    public async Task<IActionResult> Index()
    {
        var groups = await _context.Groups
            .Include(g => g.GroupCompanies).ThenInclude(gc => gc.Company)
            .Include(g => g.GroupStudents)
            .OrderByDescending(g => g.Id)
            .ToListAsync();

        var groupConfigs = await _context.GroupCourseConfigs
            .Include(c => c.Course).ThenInclude(c => c!.DocumentTemplates)
            .ToListAsync();

        var students = await _context.Users
            .Where(u => u.Role == UserRole.Обучающийся)
            .ToDictionaryAsync(u => u.Id);

        var commission = await _context.CommissionMembers
            .OrderBy(c => c.LastName)
            .ToListAsync();

        var progresses = await _context.CourseProgresses
            .Where(p => !string.IsNullOrEmpty(p.CertificateNumber))
            .ToListAsync();

        var progressMap = progresses.ToDictionary(
            p => $"{p.StudentId}_{p.CourseId}_{p.GroupId ?? 0}",
            p => p.CertificateNumber);

        ViewBag.GroupConfigs = groupConfigs;
        ViewBag.Students = students;
        ViewBag.Commission = commission;
        ViewBag.ProgressMap = progressMap;

        var finalTestMaterials = await _context.CourseMaterials
            .Where(m => m.Type == MaterialType.Test && m.TestKind == TestStatus.Итоговый)
            .ToDictionaryAsync(m => m.Id, m => m.CourseId);

        var passedAttempts = await _context.TestAttempts
            .Where(a => a.Status == AttemptStatus.Passed && finalTestMaterials.Keys.Contains(a.CourseMaterialId))
            .Select(a => new { a.StudentId, a.GroupId, a.CourseMaterialId })
            .ToListAsync();

        var passedTestsSet = new HashSet<string>();
        foreach (var attempt in passedAttempts)
        {
            if (finalTestMaterials.TryGetValue(attempt.CourseMaterialId, out int courseId))
            {
                passedTestsSet.Add($"{attempt.StudentId}_{courseId}_{attempt.GroupId}");
            }
        }

        ViewBag.PassedFinalTests = passedTestsSet;
        return View(groups);
    }

    // 2. Генерация документа (Дипломы/Протоколы)
    [HttpPost]
    public async Task<IActionResult> GenerateCourseReport(
        int groupConfigId,
        int templateId,
        bool splitByCompany,
        string? commissionOrderNumber,
        string? commissionOrderDate,
        string? chairmanInput,
        string? member1Input,
        string? member2Input)
    {
        var config = await _context.GroupCourseConfigs
            .Include(c => c.Course)
            .Include(c => c.Group)
            .FirstOrDefaultAsync(c => c.Id == groupConfigId);

        if (config == null) return NotFound("Конфигурация курса не найдена.");

        var template = await _context.CourseDocumentTemplates.FindAsync(templateId);
        if (template == null) return NotFound("Шаблон не найден.");

        var studentIds = config.AllowedStudentIds?
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse).ToList() ?? new List<int>();

        if (!studentIds.Any() && config.Group != null)
        {
            studentIds = await _context.GroupStudents
                .Where(gs => gs.GroupId == config.GroupId)
                .Select(gs => gs.StudentId)
                .ToListAsync();
        }

        var students = await _context.Users
            .Include(u => u.Company)
            .Where(u => studentIds.Contains(u.Id))
            .OrderBy(u => u.LastName)
            .ToListAsync();

        if (!students.Any()) return BadRequest("В группе нет студентов для формирования отчёта.");

        var progresses = await _context.CourseProgresses
            .Where(p => p.CourseId == config.CourseId && p.GroupId == config.GroupId && studentIds.Contains(p.StudentId))
            .ToListAsync();

        var progressDict = progresses.ToDictionary(p => p.StudentId);

        var studentDataList = students.Select(s => new StudentDocumentData
        {
            User = s,
            Progress = progressDict.GetValueOrDefault(s.Id)
        }).ToList();

        var chairman = await ResolveCommissionMemberAsync(chairmanInput);
        var member1 = await ResolveCommissionMemberAsync(member1Input);
        var member2 = await ResolveCommissionMemberAsync(member2Input);

        var docService = new DocumentGenerationService(_env.WebRootPath);
        string baseUrl = $"{Request.Scheme}://{Request.Host}";

        var groups = studentDataList.GroupBy(s => s.User.Company?.Name ?? "Частное лицо").ToList();

        if (!splitByCompany)
        {
            if (template.IsPerStudent)
            {
                using var zipStream = new MemoryStream();
                using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
                {
                    foreach (var item in studentDataList)
                    {
                        var singleList = new List<StudentDocumentData> { item };
                        byte[] bytes = await docService.GenerateDocxReportAsync(config, template, singleList, chairman, member1, member2, commissionOrderNumber, commissionOrderDate, baseUrl);

                        string entryName = $"{item.User.LastName}_{item.User.FirstName}_{template.Name}.docx";
                        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                        using var entryStream = entry.Open();
                        await entryStream.WriteAsync(bytes, 0, bytes.Length);
                    }
                }
                return File(zipStream.ToArray(), "application/zip", $"{template.Name}_{config.Group?.GroupNumber}.zip");
            }
            else
            {
                byte[] bytes = await docService.GenerateDocxReportAsync(config, template, studentDataList, chairman, member1, member2, commissionOrderNumber, commissionOrderDate, baseUrl);
                return File(bytes, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", $"{template.Name}_{config.Group?.GroupNumber}.docx");
            }
        }
        else
        {
            using var zipStream = new MemoryStream();
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
            {
                foreach (var companyGroup in groups)
                {
                    var companyStudents = companyGroup.ToList();
                    string companyName = companyGroup.Key;

                    if (template.IsPerStudent)
                    {
                        foreach (var item in companyStudents)
                        {
                            var singleList = new List<StudentDocumentData> { item };
                            byte[] bytes = await docService.GenerateDocxReportAsync(config, template, singleList, chairman, member1, member2, commissionOrderNumber, commissionOrderDate, baseUrl);

                            string entryName = $"{companyName}/{item.User.LastName}_{item.User.FirstName}_{template.Name}.docx";
                            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                            using var entryStream = entry.Open();
                            await entryStream.WriteAsync(bytes, 0, bytes.Length);
                        }
                    }
                    else
                    {
                        byte[] bytes = await docService.GenerateDocxReportAsync(config, template, companyStudents, chairman, member1, member2, commissionOrderNumber, commissionOrderDate, baseUrl);

                        string entryName = $"{companyName}_{template.Name}.docx";
                        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                        using var entryStream = entry.Open();
                        await entryStream.WriteAsync(bytes, 0, bytes.Length);
                    }
                }
            }
            return File(zipStream.ToArray(), "application/zip", $"{template.Name}_{config.Group?.GroupNumber}_по_компаниям.zip");
        }
    }

    [HttpPost]
    public async Task<IActionResult> AssignCertificates(int groupId)
    {
        var group = await _context.Groups
            .Include(g => g.GroupStudents)
            .FirstOrDefaultAsync(g => g.Id == groupId);

        if (group == null)
            return Json(new { success = false, message = "Группа не найдена." });

        var configs = await _context.GroupCourseConfigs
            .Include(c => c.Course)
            .Where(c => c.GroupId == groupId)
            .ToListAsync();

        if (!configs.Any())
            return Json(new { success = false, message = "В группе нет привязанных курсов." });

        var allowedMintrudIds = new[] { 8, 10 };
        bool hasBlockingMintrudCourses = configs.Any(c =>
            c.Course != null &&
            c.Course.MintrudProgramId != null &&
            !allowedMintrudIds.Contains(c.Course.MintrudProgramId.Value));

        if (hasBlockingMintrudCourses)
        {
            return Json(new { success = false, message = "Присвоение внутренних номеров заблокировано! В группе есть курсы Минтруда (кроме Высоты и ОЗП), для них выдаются только государственные номера." });
        }

        int targetYear = group.EndDate.Year;

        var existingCertsForYear = await _context.CourseProgresses
            .Include(p => p.Group)
            .Where(p => !string.IsNullOrEmpty(p.CertificateNumber) &&
                        p.CertificateNumber.Contains("-") &&
                        p.Group != null &&
                        p.Group.EndDate.Year == targetYear)
            .Select(p => p.CertificateNumber)
            .ToListAsync();

        int globalMaxSeq = 0;
        foreach (var cert in existingCertsForYear)
        {
            var parts = cert!.Split('-');
            if (parts.Length >= 2 && int.TryParse(parts[0], out int seq))
            {
                if (seq > globalMaxSeq) globalMaxSeq = seq;
            }
        }

        var mtNumbers = await _context.CourseProgresses
            .Where(p => !string.IsNullOrEmpty(p.MintrudNumber))
            .Select(p => p.MintrudNumber)
            .ToListAsync();

        int mtMaxSeq = 0;
        foreach (var mt in mtNumbers)
        {
            if (int.TryParse(mt, out int n) && n > mtMaxSeq)
            {
                mtMaxSeq = n;
            }
        }

        int nextSeq = globalMaxSeq + 1;
        int nextMtSeq = mtMaxSeq + 1;
        int totalAssigned = 0;
        int totalMtAssigned = 0;
        string groupNumber = group.GroupNumber;

        foreach (var config in configs)
        {
            var studentIds = config.AllowedStudentIds?
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(int.Parse).ToList() ?? new List<int>();

            if (!studentIds.Any() && group.GroupStudents != null)
            {
                studentIds = group.GroupStudents.Select(gs => gs.StudentId).ToList();
            }

            if (!studentIds.Any()) continue;

            var sortedStudents = await _context.Users
                .Where(u => studentIds.Contains(u.Id))
                .OrderBy(u => u.LastName)
                .ThenBy(u => u.FirstName)
                .ThenBy(u => u.MiddleName)
                .ToListAsync();

            bool isMintrudCourse = config.Course != null && config.Course.MintrudProgramId != null;

            foreach (var student in sortedStudents)
            {
                var progress = await _context.CourseProgresses
                    .FirstOrDefaultAsync(p =>
                        p.CourseId == config.CourseId &&
                        p.StudentId == student.Id &&
                        p.GroupId == groupId);

                if (progress == null)
                {
                    progress = new CourseProgress
                    {
                        CourseId = config.CourseId,
                        StudentId = student.Id,
                        GroupId = groupId
                    };
                    _context.CourseProgresses.Add(progress);
                }

                if (isMintrudCourse)
                {
                    if (string.IsNullOrEmpty(progress.MintrudNumber))
                    {
                        progress.MintrudNumber = nextMtSeq.ToString();
                        nextMtSeq++;
                        totalMtAssigned++;
                    }
                }
                else
                {
                    if (string.IsNullOrEmpty(progress.CertificateNumber))
                    {
                        progress.CertificateNumber = $"{nextSeq}-{groupNumber}";
                        nextSeq++;
                        totalAssigned++;
                    }
                }
            }
        }

        await _context.SaveChangesAsync();

        if (totalAssigned == 0 && totalMtAssigned == 0)
            return Json(new { success = true, message = "У всех студентов уже есть номера." });

        string msg = "";
        if (totalAssigned > 0) msg += $"Обычных: {totalAssigned}. ";
        if (totalMtAssigned > 0) msg += $"Минтруд: {totalMtAssigned}.";

        return Json(new { success = true, message = $"Успешно присвоено: {msg}" });
    }

    [HttpGet]
    public async Task<IActionResult> GetStudentTestAttempts(int studentId, int courseId, int groupId)
    {
        var attempts = await _context.TestAttempts
            .Include(a => a.CourseMaterial)
            .Where(a => a.StudentId == studentId
                     && a.CourseMaterial!.CourseId == courseId
                     && a.GroupId == groupId)
            .ToListAsync();

        var result = attempts.Select(a => new
        {
            a.Id,
            TestName = a.CourseMaterial!.Title,
            Status = a.Status.ToString(),
            a.ScorePercentage,
            a.CorrectAnswersCount,
            a.TotalQuestionsCount,
            FinishedAt = a.FinishedAt.HasValue
                ? a.FinishedAt.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
                : "Не завершен"
        }).ToList();

        return Json(result);
    }

    [HttpPost]
    public async Task<IActionResult> ExportGroupsToMintrudXml([FromForm] List<int> groupIds)
    {
        if (groupIds == null || !groupIds.Any())
            return BadRequest(new { success = false, message = "Не выбрано ни одной группы." });

        var allRecords = new List<(GroupCourseConfig config, CourseProgress progress, SdoApp.Models.Group group)>();
        var errors = new List<string>();

        foreach (var groupId in groupIds)
        {
            var group = await _context.Groups
                .Include(g => g.GroupStudents)
                .FirstOrDefaultAsync(g => g.Id == groupId);

            if (group == null) { errors.Add($"Группа с ID {groupId} не найдена."); continue; }

            var configs = await _context.GroupCourseConfigs
                .Include(c => c.Course).ThenInclude(c => c!.MintrudProgram)
                .Where(c => c.GroupId == groupId)
                .ToListAsync();

            if (!configs.Any()) { errors.Add($"В группе №{group.GroupNumber} нет привязанных курсов."); continue; }

            bool allMintrud = configs.All(c => c.Course != null && c.Course.MintrudProgramId != null);
            if (!allMintrud) { errors.Add($"В группе №{group.GroupNumber} есть курсы, не относящиеся к программам Минтруда."); continue; }

            foreach (var config in configs)
            {
                var studentIds = config.AllowedStudentIds?
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(int.Parse).ToList() ?? new List<int>();

                if (!studentIds.Any() && group.GroupStudents != null)
                    studentIds = group.GroupStudents.Select(gs => gs.StudentId).ToList();

                if (!studentIds.Any()) continue;

                var progresses = await _context.CourseProgresses
                    .Include(p => p.Student).ThenInclude(s => s!.Company)
                    .Where(p => p.CourseId == config.CourseId && p.GroupId == groupId && studentIds.Contains(p.StudentId))
                    .ToListAsync();

                foreach (var p in progresses)
                {
                    allRecords.Add((config, p, group));
                }
            }
        }

        if (errors.Any()) return BadRequest(new { success = false, message = string.Join(" ", errors) });
        if (!allRecords.Any()) return BadRequest(new { success = false, message = "Нет записей для формирования XML." });
        if (allRecords.Any(r => string.IsNullOrEmpty(r.progress.MintrudNumber)))
            return BadRequest(new { success = false, message = "У некоторых студентов нет присвоенного номера Минтруда." });

        var registryRecords = allRecords.Select(item =>
            new XElement("RegistryRecord",
                new XAttribute("outerId", item.progress.MintrudNumber),
                new XElement("Worker",
                    new XElement("LastName", item.progress.Student!.LastName ?? ""),
                    new XElement("FirstName", item.progress.Student.FirstName ?? ""),
                    new XElement("MiddleName", item.progress.Student.MiddleName ?? ""),
                    new XElement("Snils", item.progress.Student.Snils ?? ""),
                    new XElement("Citizenship", "Россия"),
                    new XElement("Position", item.progress.Student.Position ?? ""),
                    new XElement("EmployerInn", item.progress.Student.Company?.Inn ?? ""),
                    new XElement("EmployerTitle", item.progress.Student.Company?.Name ?? "")
                ),
                new XElement("Organization",
                    new XElement("Inn", "7017999506"),
                    new XElement("Title", "АНО ДПО \"ЭНЕРГЕТИК\"")
                ),
                new XElement("Test",
                    new XAttribute("isPassed", "true"),
                    new XAttribute("learnProgramId", item.config.Course!.MintrudProgram!.NumberProgram ?? ""),
                    new XElement("Date", item.config.CourseEndDate?.ToString("yyyy-MM-dd") ?? item.group.EndDate.ToString("yyyy-MM-dd")),
                    new XElement("ProtocolNumber", item.config.ProtocolNumber ?? ""),
                    new XElement("LearnProgramTitle", item.config.Course.MintrudProgram.Name ?? "")
                )
            )
        );

        var xDoc = new XDocument(
            new XElement("RegistrySet",
                new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
                registryRecords
            )
        );

        using var stream = new MemoryStream();
        xDoc.Save(stream);
        stream.Position = 0;

        string fileName = $"EISOT_Группы_{string.Join("_", groupIds)}.xml";
        Response.Headers["Content-Disposition"] =
            $"attachment; filename=\"{System.Text.Encoding.UTF8.GetString(System.Text.Encoding.GetEncoding("ISO-8859-1").GetBytes(fileName))}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";

        return File(stream.ToArray(), "application/xml");
    }

    [HttpPost]
    public async Task<IActionResult> ExportGroupsToFrdoExcel([FromForm] List<int> groupIds)
    {
        if (groupIds == null || !groupIds.Any())
            return BadRequest(new { success = false, message = "Не выбрано ни одной группы." });

        var errors = new List<string>();
        var studentsToExport = new List<(User student, SdoApp.Models.Group group, Course course, GroupCourseConfig config, CourseProgress progress)>();
        var allowedMintrudIds = new[] { 8, 10 };

        foreach (var groupId in groupIds)
        {
            var group = await _context.Groups
                .Include(g => g.GroupStudents)
                .FirstOrDefaultAsync(g => g.Id == groupId);

            if (group == null) { errors.Add($"Группа с ID {groupId} не найдена."); continue; }

            var configs = await _context.GroupCourseConfigs
                .Include(c => c.Course)
                .Where(c => c.GroupId == groupId)
                .ToListAsync();

            if (!configs.Any()) { errors.Add($"В группе №{group.GroupNumber} нет привязанных курсов."); continue; }

            bool hasBlockingMintrud = configs.Any(c =>
                c.Course != null &&
                c.Course.MintrudProgramId != null &&
                !allowedMintrudIds.Contains(c.Course.MintrudProgramId.Value));

            if (hasBlockingMintrud)
            {
                errors.Add($"В группе №{group.GroupNumber} есть курсы Минтруда, не относящиеся к ОЗП или Высоте.");
                continue;
            }

            foreach (var config in configs)
            {
                var studentIds = config.AllowedStudentIds?
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(int.Parse).ToList() ?? new List<int>();

                if (!studentIds.Any() && group.GroupStudents != null)
                    studentIds = group.GroupStudents.Select(gs => gs.StudentId).ToList();

                if (!studentIds.Any()) continue;

                var progresses = await _context.CourseProgresses
                    .Include(p => p.Student)
                    .Where(p => p.CourseId == config.CourseId && p.GroupId == groupId && studentIds.Contains(p.StudentId))
                    .ToListAsync();

                foreach (var p in progresses)
                {
                    if (string.IsNullOrEmpty(p.CertificateNumber))
                    {
                        errors.Add($"У студента {p.Student?.LastName} {p.Student?.FirstName} нет присвоенного номера удостоверения.");
                        continue;
                    }

                    studentsToExport.Add((p.Student!, group, config.Course!, config, p));
                }
            }
        }

        if (errors.Any()) return BadRequest(new { success = false, message = string.Join(" ", errors) });
        if (!studentsToExport.Any()) return BadRequest(new { success = false, message = "Нет данных для выгрузки." });

        string templatePath = Path.Combine(_env.WebRootPath, "fis-frdo.xlsx");
        if (!System.IO.File.Exists(templatePath))
            return BadRequest(new { success = false, message = "Файл шаблона fis-frdo.xlsx не найден." });

        using var workbook = new XLWorkbook(templatePath);
        var worksheet = workbook.Worksheet(1);
        int row = 2;

        foreach (var item in studentsToExport)
        {
            var student = item.student;
            var group = item.group;
            var course = item.course;
            var progress = item.progress;
            var config = item.config;

            var endDate = config.CourseEndDate ?? group.EndDate;
            var startDate = config.CourseStartDate ?? group.StartDate;

            worksheet.Cell(row, 1).Value = "Удостоверение о повышении квалификации";
            worksheet.Cell(row, 2).Value = "Оригинал";
            worksheet.Cell(row, 3).Value = "Нет";
            worksheet.Cell(row, 4).Value = "Нет";
            worksheet.Cell(row, 5).Value = "Нет";
            worksheet.Cell(row, 6).Value = "нет";
            worksheet.Cell(row, 7).Value = progress.CertificateNumber;
            worksheet.Cell(row, 8).Value = endDate.ToString("dd.MM.yyyy");
            worksheet.Cell(row, 9).Value = progress.CertificateNumber;
            worksheet.Cell(row, 10).Value = "Повышение квалификации";
            worksheet.Cell(row, 11).Value = course.Title;
            worksheet.Cell(row, 14).Value = "нет";
            worksheet.Cell(row, 19).Value = startDate.Year;
            worksheet.Cell(row, 20).Value = endDate.Year;
            if (course.DurationHours.HasValue)
                worksheet.Cell(row, 21).Value = course.DurationHours.Value;
            worksheet.Cell(row, 22).Value = student.LastName;
            worksheet.Cell(row, 23).Value = student.FirstName;
            worksheet.Cell(row, 24).Value = student.MiddleName ?? "";
            if (student.BirthDate.HasValue)
                worksheet.Cell(row, 25).Value = student.BirthDate.Value.ToString("dd.MM.yyyy");
            worksheet.Cell(row, 26).Value = student.Gender == Gender.Муж ? "Муж" : "Жен";
            worksheet.Cell(row, 27).Value = student.Snils ?? "";
            worksheet.Cell(row, 28).Value = "Очная";
            worksheet.Cell(row, 29).Value = "Платное обучение";
            worksheet.Cell(row, 30).Value = "в образовательной организации";
            worksheet.Cell(row, 31).Value = "643";
            row++;
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        string fileName = $"Отчет_ФРДО_{DateTime.Today:yyyy-MM-dd}.xlsx";
        var contentDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileNameStar = fileName };
        Response.Headers[Microsoft.Net.Http.Headers.HeaderNames.ContentDisposition] = contentDisposition.ToString();

        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    }

    [HttpPost]
    public async Task<IActionResult> ExportGroupsStatistics([FromForm] List<int> groupIds)
    {
        if (groupIds == null || !groupIds.Any())
            return BadRequest(new { success = false, message = "Не выбрано ни одной группы." });

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Статистика");
        string[] headers = { "Курс", "Группа", "Сроки обучения", "Слушатель", "Компания", "Подразделение", "Должность", "Дата сдачи итогового теста" };

        for (int i = 0; i < headers.Length; i++)
        {
            var cell = worksheet.Cell(1, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F0F4FF");
            cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        }

        int row = 2;
        bool hasData = false;

        foreach (var groupId in groupIds)
        {
            var group = await _context.Groups.FindAsync(groupId);
            if (group == null) continue;

            var configs = await _context.GroupCourseConfigs.Include(c => c.Course).Where(c => c.GroupId == groupId).ToListAsync();
            if (!configs.Any()) continue;

            foreach (var config in configs)
            {
                var studentIds = config.AllowedStudentIds?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList() ?? new List<int>();
                if (!studentIds.Any())
                {
                    studentIds = await _context.GroupStudents.Where(gs => gs.GroupId == groupId).Select(gs => gs.StudentId).ToListAsync();
                }

                var students = await _context.Users.Include(u => u.Company).Where(u => studentIds.Contains(u.Id)).OrderBy(u => u.LastName).ToListAsync();
                var finalTest = await _context.CourseMaterials.FirstOrDefaultAsync(m => m.CourseId == config.CourseId && m.Type == MaterialType.Test && m.TestKind == TestStatus.Итоговый);
                var startDateStr = config.CourseStartDate?.ToString("dd.MM.yyyy") ?? group.StartDate.ToString("dd.MM.yyyy");
                var endDateStr = config.CourseEndDate?.ToString("dd.MM.yyyy") ?? group.EndDate.ToString("dd.MM.yyyy");
                var datesRange = $"{startDateStr} - {endDateStr}";

                foreach (var student in students)
                {
                    hasData = true;
                    string testDateStr = "";
                    if (finalTest != null)
                    {
                        var firstSuccessAttempt = await _context.TestAttempts
                            .Where(a => a.CourseMaterialId == finalTest.Id && a.StudentId == student.Id && a.Status == AttemptStatus.Passed)
                            .OrderBy(a => a.FinishedAt)
                            .FirstOrDefaultAsync();

                        if (firstSuccessAttempt != null && firstSuccessAttempt.FinishedAt.HasValue)
                        {
                            testDateStr = firstSuccessAttempt.FinishedAt.Value.ToString("dd.MM.yyyy");
                        }
                    }

                    worksheet.Cell(row, 1).Value = config.Course?.Title ?? "Без названия";
                    worksheet.Cell(row, 2).Value = group.GroupNumber;
                    worksheet.Cell(row, 3).Value = datesRange;
                    worksheet.Cell(row, 4).Value = $"{student.LastName} {student.FirstName} {student.MiddleName}".Trim();
                    worksheet.Cell(row, 5).Value = student.Company?.Name ?? "Частное лицо";
                    worksheet.Cell(row, 6).Value = student.Department ?? "—";
                    worksheet.Cell(row, 7).Value = student.Position ?? "—";
                    worksheet.Cell(row, 8).Value = testDateStr;
                    worksheet.Range(row, 1, row, 8).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    worksheet.Range(row, 1, row, 8).Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                    row++;
                }
            }
        }

        if (!hasData) return BadRequest(new { success = false, message = "Нет данных для выгрузки статистики." });
        worksheet.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        string fileName = $"Статистика_по_группам_{DateTime.Today:yyyy-MM-dd}.xlsx";
        var contentDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileNameStar = fileName };
        Response.Headers[Microsoft.Net.Http.Headers.HeaderNames.ContentDisposition] = contentDisposition.ToString();

        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    }

    private async Task<(string Name, string Position)> ResolveCommissionMemberAsync(string? input)
    {
        if (string.IsNullOrEmpty(input)) return ("—", "—");

        if (int.TryParse(input, out int id))
        {
            var member = await _context.CommissionMembers.FindAsync(id);
            if (member != null)
            {
                return ($"{member.LastName} {member.FirstName} {member.MiddleName}".Trim(), member.Position);
            }
        }

        if (input.Contains(','))
        {
            var parts = input.Split(',', 2);
            return (parts[0].Trim(), parts[1].Trim());
        }

        return (input.Trim(), "—");
    }

    [HttpGet]
    public async Task<IActionResult> DownloadTestResult(int attemptId)
    {
        var attempt = await _context.TestAttempts
            .Include(a => a.CourseMaterial).ThenInclude(m => m!.Course)
            .Include(a => a.Student).ThenInclude(u => u!.Company)
            .FirstOrDefaultAsync(a => a.Id == attemptId);

        if (attempt == null) return NotFound("Протокол тестирования не найден.");

        var studentAnswers = new Dictionary<int, List<int>>();
        if (!string.IsNullOrEmpty(attempt.StudentAnswersJson))
        {
            try
            {
                studentAnswers = System.Text.Json.JsonSerializer.Deserialize<Dictionary<int, List<int>>>(attempt.StudentAnswersJson)
                                 ?? new Dictionary<int, List<int>>();
            }
            catch { }
        }

        var answeredQuestionIds = studentAnswers.Keys.ToList();
        var questions = await _context.TestQuestions.Include(q => q.Answers).Where(q => answeredQuestionIds.Contains(q.Id)).ToListAsync();

        var memoryStream = new MemoryStream();
        {
            using (var doc = XceedDocX.Create(memoryStream))
            {
                doc.SetDefaultFont(new XceedFont("Times New Roman"), 12);

                var pHeader = doc.InsertParagraph("АВТОНОМНАЯ НЕКОММЕРЧЕСКАЯ ОРГАНИЗАЦИЯ ДОПОЛНИТЕЛЬНОГО\nПРОФЕССИОНАЛЬНОГО ОБРАЗОВАНИЯ \"ЭНЕРГЕТИК\"");
                pHeader.Alignment = XceedAlignment.center;
                pHeader.FontSize(10).Bold();
                doc.InsertParagraph("\n");

                var pTitle = doc.InsertParagraph($"ИТОГОВЫЙ ЭКЗАМЕН № {attempt.Id}-{attempt.StudentId}");
                pTitle.Alignment = XceedAlignment.center;
                pTitle.FontSize(14).Bold();

                var pDate = doc.InsertParagraph($"«{attempt.StartedAt.ToLocalTime().ToString("dd")}» {attempt.StartedAt.ToLocalTime().ToString("MMMM yyyy")} г.");
                pDate.Alignment = XceedAlignment.right;
                pDate.FontSize(11).Italic();
                doc.InsertParagraph("\n");

                string studentFullName = $"{attempt.Student?.LastName} {attempt.Student?.FirstName} {attempt.Student?.MiddleName}";
                var pInfo = doc.InsertParagraph();
                pInfo.AppendLine($"Ф.И.О. слушателя: ").Bold().Append(studentFullName);
                pInfo.AppendLine($"Организация: ").Bold().Append(attempt.Student?.Company?.Name);
                pInfo.AppendLine($"Подразделение: ").Bold().Append(attempt.Student?.Department ?? "-");
                pInfo.AppendLine($"Должность слушателя: ").Bold().Append(attempt.Student?.Position ?? "-");
                pInfo.AppendLine($"Программа обучения: ").Bold().Append(attempt.CourseMaterial?.Course?.Title ?? "—");
                pInfo.AppendLine($"Дата и время начала тестирования: ").Bold().Append(attempt.StartedAt.ToLocalTime().ToString("dd.MM.yyyy  HH:mm:ss"));
                pInfo.AppendLine($"Дата и время окончания тестирования: ").Bold().Append(attempt.FinishedAt?.ToLocalTime().ToString("dd.MM.yyyy  HH:mm:ss"));
                pInfo.SpacingAfter(20);

                int rowsCount = questions.Count + 1;
                var table = doc.AddTable(rowsCount, 4);
                table.Alignment = XceedAlignment.center;
                table.Design = XceedTableDesign.TableGrid;

                table.Rows[0].Cells[0].Paragraphs[0].Append("№").Bold().Alignment = XceedAlignment.center;
                table.Rows[0].Cells[1].Paragraphs[0].Append("Вопрос").Bold().Alignment = XceedAlignment.center;
                table.Rows[0].Cells[2].Paragraphs[0].Append("Ответ слушателя").Bold().Alignment = XceedAlignment.center;
                table.Rows[0].Cells[3].Paragraphs[0].Append("Результат").Bold().Alignment = XceedAlignment.center;

                int idx = 1;
                foreach (var q in questions)
                {
                    var row = table.Rows[idx];
                    row.Cells[0].Paragraphs[0].Append(idx.ToString()).Alignment = XceedAlignment.center;
                    row.Cells[1].Paragraphs[0].Append(q.QuestionText ?? "—");

                    studentAnswers.TryGetValue(q.Id, out var selectedOptionIds);
                    selectedOptionIds ??= new List<int>();

                    var answerCell = row.Cells[2];
                    var firstParagraph = answerCell.Paragraphs[0];
                    if (firstParagraph.Text.Length > 0) firstParagraph.RemoveText(0, firstParagraph.Text.Length);

                    if (!selectedOptionIds.Any())
                    {
                        firstParagraph.Append("Нет ответа").Italic();
                    }
                    else
                    {
                        int currentOptIdx = 0;
                        foreach (var optId in selectedOptionIds)
                        {
                            var option = q.Answers.FirstOrDefault(a => a.Id == optId);
                            if (option != null)
                            {
                                if (currentOptIdx == 0) firstParagraph.Append($"• {option.AnswerText}");
                                else answerCell.InsertParagraph($"• {option.AnswerText}");
                                currentOptIdx++;
                            }
                        }
                    }

                    var correctOptionIds = q.Answers.Where(a => a.IsCorrect).Select(a => a.Id).ToList();
                    bool isUserCorrect = correctOptionIds.Count == selectedOptionIds.Count && !correctOptionIds.Except(selectedOptionIds).Any();
                    row.Cells[3].Paragraphs[0].Append(isUserCorrect ? "Верно" : "Неверно").Alignment = XceedAlignment.center;
                    idx++;
                }

                table.SetWidths(new float[] { 40f, 260f, 200f, 90f });
                doc.InsertTable(table);
                doc.InsertParagraph("\n");

                int totalQuestionsCount = attempt.TotalQuestionsCount > 0 ? attempt.TotalQuestionsCount : questions.Count;
                int errorsCount = totalQuestionsCount - attempt.CorrectAnswersCount;
                if (errorsCount < 0) errorsCount = 0;
                int errorsPercentage = totalQuestionsCount > 0 ? (int)Math.Round((double)errorsCount / totalQuestionsCount * 100) : 0;
                int allowedErrorsPercentage = 100 - attempt.CourseMaterial!.PassPercentage;
                int allowedErrorsCount = (int)Math.Floor((double)totalQuestionsCount * allowedErrorsPercentage / 100);

                doc.InsertParagraph($"Итого вопросов: {totalQuestionsCount}");
                doc.InsertParagraph($"Допустимое количество ошибок: {allowedErrorsCount} ({allowedErrorsPercentage} %)");
                doc.InsertParagraph($"Допущено ошибок: {errorsCount} ({errorsPercentage} %)");
                doc.InsertParagraph($"Верных ответов: {attempt.CorrectAnswersCount} ({attempt.ScorePercentage} %)");
                doc.InsertParagraph("\n");

                string resultStatusText = attempt.Status == AttemptStatus.Passed ? "СДАНО" : "НЕ СДАНO";
                var pFinalResult = doc.InsertParagraph("Результат тестирования: ");
                pFinalResult.Append(resultStatusText).Bold();
                pFinalResult.FontSize(12);
                doc.InsertParagraph("\n");
                doc.InsertParagraph("При проведении тестирования нарушений его порядка не зафиксировано.");
                doc.InsertParagraph("\n\n");

                var pSignAdmin = doc.InsertParagraph("Ответственный за проведение тестирования  _________________ / _________________");
                pSignAdmin.SpacingAfter(25);
                string shortStudentName = $"{attempt.Student?.LastName} {attempt.Student?.FirstName.FirstOrDefault()}.{attempt.Student?.MiddleName?.FirstOrDefault()}.";
                doc.InsertParagraph($"Тестируемый  _________________ / {shortStudentName}");
                doc.Save();
            }

            string fileName = $"Protocol_{attempt.Id}_{attempt.Student?.LastName}.docx";
            memoryStream.Position = 0;
            return File(memoryStream, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", fileName);
        }
    }

    [HttpPost]
    public async Task<IActionResult> ImportMintrudNumbers(IFormFile excelFile)
    {
        if (excelFile == null || excelFile.Length == 0)
            return Json(new { success = false, message = "Файл не выбран или пуст." });

        try
        {
            using var stream = new MemoryStream();
            await excelFile.CopyToAsync(stream);
            using var workbook = new XLWorkbook(stream);
            var worksheet = workbook.Worksheet(1);

            var headerRow = worksheet.Row(1);
            int colExternalId = headerRow.CellsUsed().First(c => c.GetString().Trim() == "Id внешней системы").Address.ColumnNumber;
            int colRegNumber = headerRow.CellsUsed().First(c => c.GetString().Trim() == "Номер в реестре").Address.ColumnNumber;

            int updatedCount = 0;
            int notFoundCount = 0;

            foreach (var row in worksheet.RowsUsed().Skip(1))
            {
                string externalId = row.Cell(colExternalId).GetString().Trim();
                string regNumber = row.Cell(colRegNumber).GetString().Trim();

                if (string.IsNullOrEmpty(externalId) || string.IsNullOrEmpty(regNumber)) continue;

                var progress = await _context.CourseProgresses.FirstOrDefaultAsync(p => p.MintrudNumber == externalId);
                if (progress != null)
                {
                    progress.MintrudOfficialNumber = regNumber;
                    updatedCount++;
                }
                else
                {
                    notFoundCount++;
                }
            }

            await _context.SaveChangesAsync();

            string message = $"Импорт завершен. Обновлено записей: {updatedCount}.";
            if (notFoundCount > 0) message += $" Не найдено в системе (пропущено): {notFoundCount}.";

            return Json(new { success = true, message = message });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Ошибка при чтении файла: {ex.Message}" });
        }
    }
}

// =========================================================================
// СЕРВИС ГЕНЕРАЦИИ ДОКУМЕНТОВ (ВОЗВРАЩЕНА СТАРАЯ ЛОГИКА)
// =========================================================================
internal class DocumentGenerationService
{
    private readonly string _webRootPath;

    public DocumentGenerationService(string webRootPath)
    {
        _webRootPath = webRootPath;
    }

    public async Task<byte[]> GenerateDocxReportAsync(
    GroupCourseConfig config,
    CourseDocumentTemplate template,
    List<StudentDocumentData> students,
    (string Name, string Position) chairman,
    (string Name, string Position) member1,
    (string Name, string Position) member2,
    string? commissionOrderNumber,
    string? commissionOrderDate,
    string baseUrl)
    {
        string templateFullPath = Path.Combine(_webRootPath, template.FilePath.TrimStart('~', '/'));
        if (!File.Exists(templateFullPath)) throw new FileNotFoundException("Шаблон не найден.");

        byte[] templateBytes = await File.ReadAllBytesAsync(templateFullPath);
        using var resultStream = new MemoryStream();
        await resultStream.WriteAsync(templateBytes, 0, templateBytes.Length);
        resultStream.Position = 0;

        using (var doc = WordprocessingDocument.Open(resultStream, true))
        {
            var body = doc.MainDocumentPart.Document.Body;

            // Проверяем: если в документе ЕСТЬ табличная строка со студенческими тегами, 
            // значит это обычный реестр/протокол.
            var templateRow = body.Descendants<TableRow>()
                .FirstOrDefault(r => r.InnerText.Contains("{{i}}") || r.InnerText.Contains("{{t.FullName}}"));

            // РЕЖИМ 1: Обычный протокол (Ваша старая рабочая логика)
            if (templateRow != null && !template.IsPerStudent)
            {
                ReplaceGlobalTags(body, config, chairman, member1, member2, commissionOrderNumber, commissionOrderDate, students[0].User.Company?.Name);

                var table = templateRow.Parent;
                var insertAfterRow = templateRow;

                for (int i = 0; i < students.Count; i++)
                {
                    var student = students[i];
                    var newRow = (TableRow)templateRow.CloneNode(true);
                    ReplaceStudentTags(newRow, student, i + 1);

                    string qrText = !string.IsNullOrEmpty(student.Progress?.MintrudOfficialNumber)
                        ? $"{baseUrl}/ohranatruda/{student.Progress.MintrudOfficialNumber}"
                        : baseUrl;
                    var qrGenerator = Net.Codecrete.QrCodeGenerator.QrCode.EncodeText(qrText, Net.Codecrete.QrCodeGenerator.QrCode.Ecc.High);
                    ReplaceTagWithImage(doc, newRow, "{{t.qr_code}}", qrGenerator.ToPngBitmap());

                    table.InsertAfter(newRow, insertAfterRow);
                    insertAfterRow = newRow;
                }
                templateRow.Remove();
            }
            // РЕЖИМ 2: НОВЫЙ РЕЖИМ (Потоковая печать карточек-удостоверений в один файл)
            else if (templateRow == null && !template.IsPerStudent)
            {
                // 1. Копируем абсолютно все узлы шаблона карточки (структуру таблицы верстки, абзацы) в память
                var templateElements = body.ChildElements
    .Where(e => e is Table || (e is Paragraph && !string.IsNullOrWhiteSpace(e.InnerText)))
    .Select(e => e.CloneNode(true))
    .ToList();

                // 2. Очищаем оригинальный документ, подготавливая его к заполнению
                body.RemoveAllChildren();

                // 3. Запускаем цикл по студентам верхнего уровня
                // =========================================================================
                // ИСПРАВЛЕННЫЙ ЦИКЛ ДЛЯ РЕЖИМА 2 (Потоковая печать карточек)
                // =========================================================================
                for (int i = 0; i < students.Count; i++)
                {
                    var student = students[i];

                    // Клонируем изолированный блок элементов для конкретного человека
                    foreach (var element in templateElements)
                    {
                        var clonedElement = element.CloneNode(true);

                        // ИСПРАВЛЕНО: Теперь передаются ВСЕ члены комиссии строго по порядку (chairman, member1, member2)
                        ReplaceGlobalTagsInBlock(
                            clonedElement,
                            config,
                            chairman,
                            member1,
                            member2,
                            commissionOrderNumber,
                            commissionOrderDate,
                            student.User.Company?.Name
                        );

                        // Затем накатываем персональные данные конкретного студента
                        ReplaceStudentTagsInBlock(clonedElement, student, i + 1);

                        // Обрабатываем индивидуальный QR-код для этой карточки
                        if (clonedElement.InnerText.Contains("{{t.qr_code}}"))
                        {
                            string qrText = !string.IsNullOrEmpty(student.Progress?.MintrudOfficialNumber)
                                ? $"{baseUrl}/ohranatruda/{student.Progress.MintrudOfficialNumber}"
                                : baseUrl;
                            var qrGenerator = Net.Codecrete.QrCodeGenerator.QrCode.EncodeText(qrText, Net.Codecrete.QrCodeGenerator.QrCode.Ecc.High);
                            ReplaceTagWithImage(doc, clonedElement, "{{t.qr_code}}", qrGenerator.ToPngBitmap());
                        }

                        // Прикрепляем готовый и полностью заполненный блок карточки к телу документа
                        body.AppendChild(clonedElement);
                    }

                    // Логика разрыва страниц: каждые 3 карточки вставляем PageBreak
                    if ((i + 1) % 3 == 0 && i < students.Count - 1)
                    {
                        body.AppendChild(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
                    }
                }
            }

            doc.MainDocumentPart.Document.Save();
        }
        return resultStream.ToArray();
    }

    private void ReplaceGlobalTagsInBlock(OpenXmlElement element, GroupCourseConfig config,
        (string Name, string Position) chairman, (string Name, string Position) member1, (string Name, string Position) member2,
        string? orderNum, string? orderDate, string? companyName)
    {
        var values = new Dictionary<string, string>
    {
        { "{{CourseTitle}}", config.Course?.Title ?? "—" },
        { "{{ProtocolNumber}}", config.ProtocolNumber ?? "—" },
        { "{{CommissionOrderNumber}}", orderNum ?? "—" },
        { "{{CommissionOrderDate}}", !string.IsNullOrEmpty(orderDate) ? DateTime.Parse(orderDate).ToString("dd.MM.yyyy") : "—" },
        { "{{EndDate}}", (config.CourseEndDate ?? config.Group?.EndDate)?.ToString("dd.MM.yyyy") ?? "—" },
        { "{{ChairmanName}}", chairman.Name },
        { "{{ChairmanPosition}}", chairman.Position },
        { "{{userCompany}}", companyName ?? "Частное лицо" },
        // Замена объема учебных часов
        { "{{DurationHours}}", config.Course?.DurationHours?.ToString() ?? "—" }
    };
        ReplaceTextInElement(element, values);
    }

    // Вспомогательный метод замены данных студента внутри блока карточки
    private void ReplaceStudentTagsInBlock(OpenXmlElement element, StudentDocumentData data, int index)
    {
        var s = data.User;
        var p = data.Progress;
        string fullName = $"{s.LastName} {s.FirstName} {s.MiddleName}".Trim();

        var values = new Dictionary<string, string>
    {
        { "{{i}}", index.ToString() },
        { "{{t.FullName}}", fullName }, { "{{FullName}}", fullName },
        { "{{t.Position}}", s.Position ?? "—" }, { "{{Position}}", s.Position ?? "—" },
        { "{{t.Company}}", s.Company?.Name ?? "Частное лицо" }, { "{{Company}}", s.Company?.Name ?? "Частное лицо" },
        { "{{t.userIdNum}}", p?.CertificateNumber ?? "—" }, { "{{userIdNum}}", p?.CertificateNumber ?? "—" },
        { "{{t.userDepartment}}", s.Department ?? "—" }, { "{{userDepartment}}", s.Department ?? "—" },
        { "{{t.eisot}}", p?.MintrudOfficialNumber ?? p?.MintrudNumber ?? "—" }, { "{{eisot}}", p?.MintrudOfficialNumber ?? p?.MintrudNumber ?? "—" }
    };
        ReplaceTextInElement(element, values);
    }

    private void ReplaceGlobalTags(Body body, GroupCourseConfig config,
        (string Name, string Position) chairman, (string Name, string Position) member1, (string Name, string Position) member2,
        string? orderNum, string? orderDate, string? companyName)
    {
        var values = new Dictionary<string, string>
        {
            { "{{CourseTitle}}", config.Course?.Title ?? "—" },
            { "{{ProtocolNumber}}", config.ProtocolNumber ?? "—" },
            { "{{CommissionOrderNumber}}", orderNum ?? "—" },
            { "{{CommissionOrderDate}}", !string.IsNullOrEmpty(orderDate) ? DateTime.Parse(orderDate).ToString("dd.MM.yyyy") : "—" },
            { "{{EndDate}}", (config.CourseEndDate ?? config.Group?.EndDate)?.ToString("dd.MM.yyyy") ?? "—" },
            { "{{ChairmanName}}", chairman.Name },
            { "{{ChairmanPosition}}", chairman.Position },
            { "{{Member1Name}}", member1.Name },
            { "{{Member1Position}}", member1.Position },
            { "{{Member2Name}}", member2.Name },
            { "{{Member2Position}}", member2.Position },
            { "{{userCompany}}", companyName ?? "Частное лицо" }
        };
        ReplaceTextInElement(body, values);
    }

    private void ReplaceStudentTags(OpenXmlElement row, StudentDocumentData data, int index)
    {
        var s = data.User;
        var p = data.Progress;
        string fullName = $"{s.LastName} {s.FirstName} {s.MiddleName}".Trim();

        var values = new Dictionary<string, string>
        {
            { "{{i}}", index.ToString() },
            { "{{t.FullName}}", fullName }, { "{{FullName}}", fullName },
            { "{{t.Position}}", s.Position ?? "—" }, { "{{Position}}", s.Position ?? "—" },
            { "{{t.Company}}", s.Company?.Name ?? "Частное лицо" }, { "{{Company}}", s.Company?.Name ?? "Частное лицо" },
            { "{{t.userIdNum}}", p?.CertificateNumber ?? "—" }, { "{{userIdNum}}", p?.CertificateNumber ?? "—" },
            { "{{t.userDepartment}}", s.Department ?? "—" }, { "{{userDepartment}}", s.Department ?? "—" },
            { "{{t.eisot}}", p?.MintrudOfficialNumber ?? p?.MintrudNumber ?? "—" }, { "{{eisot}}", p?.MintrudOfficialNumber ?? p?.MintrudNumber ?? "—" }
        };
        ReplaceTextInElement(row, values);
    }

    private void ReplaceTextInElement(OpenXmlElement element, Dictionary<string, string> values)
    {
        foreach (var p in element.Descendants<Paragraph>().ToList())
        {
            var textNodes = p.Descendants<Text>().ToList();
            if (!textNodes.Any()) continue;

            string fullText = string.Concat(textNodes.Select(t => t.Text));
            string newText = fullText;
            bool changed = false;

            foreach (var kvp in values)
            {
                if (newText.Contains(kvp.Key))
                {
                    newText = newText.Replace(kvp.Key, kvp.Value);
                    changed = true;
                }
            }

            if (changed)
            {
                textNodes[0].Text = newText;
                textNodes[0].Space = SpaceProcessingModeValues.Preserve;
                for (int i = 1; i < textNodes.Count; i++) textNodes[i].Text = "";
            }
        }
    }

    private void ReplaceTagWithImage(WordprocessingDocument doc, OpenXmlElement element, string tag, byte[] imageBytes)
    {
        foreach (var p in element.Descendants<Paragraph>().ToList())
        {
            var textNodes = p.Descendants<Text>().ToList();
            if (!textNodes.Any()) continue;

            string fullText = string.Concat(textNodes.Select(t => t.Text));
            if (!fullText.Contains(tag)) continue;

            string newText = fullText.Replace(tag, "");
            textNodes[0].Text = newText;
            textNodes[0].Space = SpaceProcessingModeValues.Preserve;
            for (int i = 1; i < textNodes.Count; i++) textNodes[i].Text = "";

            var run = new Run();
            run.AppendChild(CreateImageElement(doc, imageBytes));
            p.AppendChild(run);
        }
    }

    private Drawing CreateImageElement(WordprocessingDocument doc, byte[] imageBytes)
    {
        var imagePart = doc.MainDocumentPart.AddImagePart(ImagePartType.Png);
        using (var stream = new MemoryStream(imageBytes)) imagePart.FeedData(stream);
        string relationshipId = doc.MainDocumentPart.GetIdOfPart(imagePart);

        long widthEmu = 540000;
        long heightEmu = 540000;

        return new Drawing(
            new DW.Inline(
                new DW.Extent { Cx = widthEmu, Cy = heightEmu },
                new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new DW.DocProperties { Id = (UInt32Value)1U, Name = "QR Code" },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties { Id = (UInt32Value)0U, Name = "qr.png" },
                                new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(
                                new A.Blip { Embed = relationshipId },
                                new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(
                                new A.Transform2D(
                                    new A.Offset { X = 0L, Y = 0L },
                                    new A.Extents { Cx = widthEmu, Cy = heightEmu }),
                                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })
                        )
                    )
                    { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })
            )
            { DistanceFromTop = (UInt32Value)0U, DistanceFromBottom = (UInt32Value)0U, DistanceFromLeft = (UInt32Value)0U, DistanceFromRight = (UInt32Value)0U }
        );
    }
}