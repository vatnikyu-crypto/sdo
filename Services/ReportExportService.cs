using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models;
using ClosedXML.Excel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace SdoApp.Services;

public class ReportExportService
{
    private readonly AppDbContext _context;
    private readonly IWebHostEnvironment _env;

    public ReportExportService(AppDbContext context, IWebHostEnvironment env)
    {
        _context = context;
        _env = env;
    }

    // 1. ЭКСПОРТ В XML (МИНТРУД)
    public async Task<(bool Success, string Error, byte[]? Data, string FileName)> ExportMintrudXmlAsync(List<int> groupIds)
    {
        if (groupIds == null || !groupIds.Any()) return (false, "Не выбрано ни одной группы.", null, "");

        var allRecords = new List<(GroupCourseConfig config, CourseProgress progress, Group group)>();
        var errors = new List<string>();

        foreach (var groupId in groupIds)
        {
            var group = await _context.Groups.Include(g => g.GroupStudents).FirstOrDefaultAsync(g => g.Id == groupId);
            if (group == null) { errors.Add($"Группа ID {groupId} не найдена."); continue; }

            var configs = await _context.GroupCourseConfigs.Include(c => c.Course).ThenInclude(c => c!.MintrudProgram).Where(c => c.GroupId == groupId).ToListAsync();
            if (!configs.Any()) { errors.Add($"В группе №{group.GroupNumber} нет курсов."); continue; }

            if (!configs.All(c => c.Course != null && c.Course.MintrudProgramId != null)) { errors.Add($"В группе №{group.GroupNumber} есть курсы не Минтруда."); continue; }

            foreach (var config in configs)
            {
                var studentIds = await AccessService.GetAllowedStudentIdsAsync(_context, groupId, config.CourseId);
                if (!studentIds.Any()) continue;

                var progresses = await _context.CourseProgresses.Include(p => p.Student).ThenInclude(s => s!.Company)
                    .Where(p => p.CourseId == config.CourseId && p.GroupId == groupId && studentIds.Contains(p.StudentId)).ToListAsync();

                allRecords.AddRange(progresses.Select(p => (config, p, group)));
            }
        }

        if (errors.Any()) return (false, string.Join(" ", errors), null, "");
        if (!allRecords.Any()) return (false, "Нет записей для XML.", null, "");
        if (allRecords.Any(r => string.IsNullOrEmpty(r.progress.MintrudNumber))) return (false, "У некоторых студентов нет номера Минтруда.", null, "");

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
                new XElement("Organization", new XElement("Inn", "7017999506"), new XElement("Title", "АНО ДПО \"ЭНЕРГЕТИК\"")),
                new XElement("Test",
                    new XAttribute("isPassed", "true"),
                    new XAttribute("learnProgramId", item.config.Course!.MintrudProgram!.NumberProgram ?? ""),
                    new XElement("Date", item.config.CourseEndDate?.ToString("yyyy-MM-dd") ?? item.group.EndDate.ToString("yyyy-MM-dd")),
                    new XElement("ProtocolNumber", item.config.ProtocolNumber ?? ""),
                    new XElement("LearnProgramTitle", item.config.Course.MintrudProgram.Name ?? "")
                )
            )
        );

        var xDoc = new XDocument(new XElement("RegistrySet", new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"), registryRecords));
        using var stream = new MemoryStream();
        xDoc.Save(stream);

        return (true, "", stream.ToArray(), $"EISOT_Группы_{string.Join("_", groupIds)}.xml");
    }

    // 2. ЭКСПОРТ ФРДО (EXCEL)
    public async Task<(bool Success, string Error, byte[]? Data, string FileName)> ExportFrdoExcelAsync(List<int> groupIds)
    {
        var errors = new List<string>();
        var studentsToExport = new List<(User student, Group group, Course course, GroupCourseConfig config, CourseProgress progress)>();
        var allowedMintrudIds = new[] { 8, 10 };

        foreach (var groupId in groupIds)
        {
            var group = await _context.Groups.Include(g => g.GroupStudents).FirstOrDefaultAsync(g => g.Id == groupId);
            if (group == null) continue;

            var configs = await _context.GroupCourseConfigs.Include(c => c.Course).Where(c => c.GroupId == groupId).ToListAsync();
            if (configs.Any(c => c.Course != null && c.Course.MintrudProgramId != null && !allowedMintrudIds.Contains(c.Course.MintrudProgramId.Value)))
            {
                errors.Add($"В группе №{group.GroupNumber} есть курсы Минтруда (кроме ОЗП/Высоты)."); continue;
            }

            foreach (var config in configs)
            {
                var studentIds = await AccessService.GetAllowedStudentIdsAsync(_context, groupId, config.CourseId);
                if (!studentIds.Any()) continue;

                var progresses = await _context.CourseProgresses.Include(p => p.Student)
                    .Where(p => p.CourseId == config.CourseId && p.GroupId == groupId && studentIds.Contains(p.StudentId)).ToListAsync();

                foreach (var p in progresses)
                {
                    if (string.IsNullOrEmpty(p.CertificateNumber)) { errors.Add($"У {p.Student?.LastName} нет номера удостоверения."); continue; }
                    studentsToExport.Add((p.Student!, group, config.Course!, config, p));
                }
            }
        }

        if (errors.Any()) return (false, string.Join(" ", errors), null, "");
        if (!studentsToExport.Any()) return (false, "Нет данных для выгрузки.", null, "");

        string templatePath = Path.Combine(_env.WebRootPath, "templates", "fis-frdo.xlsx");
        if (!File.Exists(templatePath)) return (false, "Файл шаблона fis-frdo.xlsx не найден.", null, "");

        using var workbook = new XLWorkbook(templatePath);
        var worksheet = workbook.Worksheet(1);
        int row = 2;

        foreach (var item in studentsToExport)
        {
            var student = item.student; var group = item.group; var course = item.course; var progress = item.progress; var config = item.config;
            var endDate = config.CourseEndDate ?? group.EndDate;
            var startDate = config.CourseStartDate ?? group.StartDate;

            worksheet.Cell(row, 1).Value = "Удостоверение о повышении квалификации";
            worksheet.Cell(row, 2).Value = "Оригинал"; worksheet.Cell(row, 3).Value = "Нет"; worksheet.Cell(row, 4).Value = "Нет";
            worksheet.Cell(row, 5).Value = "Нет"; worksheet.Cell(row, 6).Value = "нет";
            worksheet.Cell(row, 7).Value = progress.CertificateNumber; worksheet.Cell(row, 8).Value = endDate.ToString("dd.MM.yyyy");
            worksheet.Cell(row, 9).Value = progress.CertificateNumber; worksheet.Cell(row, 10).Value = "Повышение квалификации";
            worksheet.Cell(row, 11).Value = course.Title; worksheet.Cell(row, 14).Value = "нет";
            worksheet.Cell(row, 19).Value = startDate.Year; worksheet.Cell(row, 20).Value = endDate.Year;
            if (course.DurationHours.HasValue) worksheet.Cell(row, 21).Value = course.DurationHours.Value;
            worksheet.Cell(row, 22).Value = student.LastName; worksheet.Cell(row, 23).Value = student.FirstName;
            worksheet.Cell(row, 24).Value = student.MiddleName ?? "";
            if (student.BirthDate.HasValue) worksheet.Cell(row, 25).Value = student.BirthDate.Value.ToString("dd.MM.yyyy");
            worksheet.Cell(row, 26).Value = student.Gender == Gender.Муж ? "Муж" : "Жен"; worksheet.Cell(row, 27).Value = student.Snils ?? "";
            worksheet.Cell(row, 28).Value = "Очная"; worksheet.Cell(row, 29).Value = "Платное обучение";
            worksheet.Cell(row, 30).Value = "в образовательной организации"; worksheet.Cell(row, 31).Value = "643";
            row++;
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return (true, "", stream.ToArray(), $"Отчет_ФРДО_{DateTime.Today:yyyy-MM-dd}.xlsx");
    }

    // 3. ЭКСПОРТ СТАТИСТИКИ (EXCEL)
    public async Task<(bool Success, string Error, byte[]? Data, string FileName)> ExportStatisticsExcelAsync(List<int> groupIds)
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Статистика");
        string[] headers = { "Курс", "Группа", "Сроки обучения", "Слушатель", "Компания", "Подразделение", "Должность", "Дата сдачи итогового теста" };

        for (int i = 0; i < headers.Length; i++)
        {
            var cell = worksheet.Cell(1, i + 1); cell.Value = headers[i]; cell.Style.Font.Bold = true;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center; cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F0F4FF"); cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        }

        int row = 2; bool hasData = false;

        foreach (var groupId in groupIds)
        {
            var group = await _context.Groups.FindAsync(groupId); if (group == null) continue;
            var configs = await _context.GroupCourseConfigs.Include(c => c.Course).Where(c => c.GroupId == groupId).ToListAsync();
            if (!configs.Any()) continue;

            foreach (var config in configs)
            {
                var studentIds = await AccessService.GetAllowedStudentIdsAsync(_context, groupId, config.CourseId);
                var students = await _context.Users.Include(u => u.Company).Where(u => studentIds.Contains(u.Id)).OrderBy(u => u.LastName).ToListAsync();
                var finalTest = await _context.CourseMaterials.FirstOrDefaultAsync(m => m.CourseId == config.CourseId && m.Type == MaterialType.Test && m.TestKind == TestStatus.Итоговый);
                var startDateStr = config.CourseStartDate?.ToString("dd.MM.yyyy") ?? group.StartDate.ToString("dd.MM.yyyy");
                var endDateStr = config.CourseEndDate?.ToString("dd.MM.yyyy") ?? group.EndDate.ToString("dd.MM.yyyy");
                var datesRange = $"{startDateStr} - {endDateStr}";

                foreach (var student in students)
                {
                    hasData = true; string testDateStr = "";
                    if (finalTest != null)
                    {
                        var attempt = await _context.TestAttempts.Where(a => a.CourseMaterialId == finalTest.Id && a.StudentId == student.Id && a.Status == AttemptStatus.Passed).OrderBy(a => a.FinishedAt).FirstOrDefaultAsync();
                        if (attempt?.FinishedAt != null) testDateStr = attempt.FinishedAt.Value.ToString("dd.MM.yyyy");
                    }

                    worksheet.Cell(row, 1).Value = config.Course?.Title ?? "Без названия"; worksheet.Cell(row, 2).Value = group.GroupNumber;
                    worksheet.Cell(row, 3).Value = datesRange; worksheet.Cell(row, 4).Value = $"{student.LastName} {student.FirstName} {student.MiddleName}".Trim();
                    worksheet.Cell(row, 5).Value = student.Company?.Name ?? "Частное лицо"; worksheet.Cell(row, 6).Value = student.Department ?? "—";
                    worksheet.Cell(row, 7).Value = student.Position ?? "—"; worksheet.Cell(row, 8).Value = testDateStr;
                    worksheet.Range(row, 1, row, 8).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    worksheet.Range(row, 1, row, 8).Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                    row++;
                }
            }
        }

        if (!hasData) return (false, "Нет данных для выгрузки статистики.", null, "");
        worksheet.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return (true, "", stream.ToArray(), $"Статистика_по_группам_{DateTime.Today:yyyy-MM-dd}.xlsx");
    }
}