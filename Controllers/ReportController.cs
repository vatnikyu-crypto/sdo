using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models;
using SdoApp.Models.DTO;
using SdoApp.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.IO.Compression;
using ClosedXML.Excel;

// --- АЛИАСЫ ДЛЯ XCEED ---
using XceedDocX = Xceed.Words.NET.DocX;
using XceedAlignment = Xceed.Document.NET.Alignment;
using XceedTableDesign = Xceed.Document.NET.TableDesign;
using XceedFont = Xceed.Document.NET.Font;

namespace SdoApp.Controllers;

[Authorize(Roles = "Админ")]
public class ReportController : Controller
{
    private readonly AppDbContext _context;
    private readonly ReportExportService _exportService;
    private readonly DocumentGenerationService _docService;
    private readonly AuditService _auditService;

    public ReportController(AppDbContext context, IWebHostEnvironment env, AuditService auditService)
    {
        _context = context;
        _exportService = new ReportExportService(context, env);
        _docService = new DocumentGenerationService(env.WebRootPath);
        _auditService = auditService;
    }

    // 1. Главная страница
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

    // 2. Генерация документов (Word)
    [HttpPost]
    public async Task<IActionResult> GenerateCourseReport(
        int groupConfigId, int templateId, bool splitByCompany,
        string? commissionOrderNumber, string? commissionOrderDate,
        string? chairmanInput, string? member1Input, string? member2Input)
    {
        var config = await _context.GroupCourseConfigs.Include(c => c.Course).Include(c => c.Group).FirstOrDefaultAsync(c => c.Id == groupConfigId);
        if (config == null) return NotFound("Конфигурация не найдена.");

        var template = await _context.CourseDocumentTemplates.FindAsync(templateId);
        if (template == null) return NotFound("Шаблон не найден.");

        var studentIds = await AccessService.GetAllowedStudentIdsAsync(_context, config.GroupId, config.CourseId);
        if (!studentIds.Any()) return BadRequest(new { success = false, message = "Нет студентов, допущенных к курсу." });

        var students = await _context.Users.Include(u => u.Company).Where(u => studentIds.Contains(u.Id)).OrderBy(u => u.LastName).ToListAsync();
        if (!students.Any()) return BadRequest(new { success = false, message = "В группе нет студентов." });

        var progresses = await _context.CourseProgresses.Where(p => p.CourseId == config.CourseId && p.GroupId == config.GroupId && studentIds.Contains(p.StudentId)).ToListAsync();
        var progressDict = progresses.ToDictionary(p => p.StudentId);

        var studentDataList = students.Select(s => new StudentDocumentData { User = s, Progress = progressDict.GetValueOrDefault(s.Id) }).ToList();

        var chairman = await ResolveCommissionMemberAsync(chairmanInput);
        var member1 = await ResolveCommissionMemberAsync(member1Input);
        var member2 = await ResolveCommissionMemberAsync(member2Input);

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
                        byte[] bytes = await _docService.GenerateDocxReportAsync(config, template, singleList, chairman, member1, member2, commissionOrderNumber, commissionOrderDate, baseUrl);

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
                byte[] bytes = await _docService.GenerateDocxReportAsync(config, template, studentDataList, chairman, member1, member2, commissionOrderNumber, commissionOrderDate, baseUrl);
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
                            byte[] bytes = await _docService.GenerateDocxReportAsync(config, template, singleList, chairman, member1, member2, commissionOrderNumber, commissionOrderDate, baseUrl);

                            string entryName = $"{companyName}/{item.User.LastName}_{item.User.FirstName}_{template.Name}.docx";
                            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                            using var entryStream = entry.Open();
                            await entryStream.WriteAsync(bytes, 0, bytes.Length);
                        }
                    }
                    else
                    {
                        byte[] bytes = await _docService.GenerateDocxReportAsync(config, template, companyStudents, chairman, member1, member2, commissionOrderNumber, commissionOrderDate, baseUrl);
                        string entryName = $"{companyName}_{template.Name}.docx";
                        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                        using var entryStream = entry.Open();
                        await entryStream.WriteAsync(bytes, 0, bytes.Length);
                    }
                }
            }
            await _auditService.LogAsync("Export", "Document", groupConfigId, $"Сгенерирован отчет по шаблону '{template.Name}' для группы {config.Group?.GroupNumber}");
            return File(zipStream.ToArray(), "application/zip", $"{template.Name}_{config.Group?.GroupNumber}_по_компаниям.zip");
        }
    }

    // 3. Присвоение номеров
    [HttpPost]
    public async Task<IActionResult> AssignCertificates(int groupId)
    {
        var group = await _context.Groups.Include(g => g.GroupStudents).FirstOrDefaultAsync(g => g.Id == groupId);
        if (group == null) return Json(new { success = false, message = "Группа не найдена." });

        var configs = await _context.GroupCourseConfigs.Include(c => c.Course).Where(c => c.GroupId == groupId).ToListAsync();
        if (!configs.Any()) return Json(new { success = false, message = "В группе нет курсов." });

        var allowedMintrudIds = new[] { 8, 10 };
        if (configs.Any(c => c.Course != null && c.Course.MintrudProgramId != null && !allowedMintrudIds.Contains(c.Course.MintrudProgramId.Value)))
            return Json(new { success = false, message = "Присвоение внутренних номеров заблокировано (есть курсы Минтруда)." });

        int targetYear = group.EndDate.Year;
        var existingCertsForYear = await _context.CourseProgresses.Include(p => p.Group).Where(p => !string.IsNullOrEmpty(p.CertificateNumber) && p.CertificateNumber.Contains("-") && p.Group != null && p.Group.EndDate.Year == targetYear).Select(p => p.CertificateNumber).ToListAsync();
        int globalMaxSeq = 0;
        foreach (var cert in existingCertsForYear)
        {
            var parts = cert!.Split('-');
            if (parts.Length >= 2 && int.TryParse(parts[0], out int seq) && seq > globalMaxSeq) globalMaxSeq = seq;
        }

        var mtNumbers = await _context.CourseProgresses.Where(p => !string.IsNullOrEmpty(p.MintrudNumber)).Select(p => p.MintrudNumber).ToListAsync();
        int mtMaxSeq = 0;
        foreach (var mt in mtNumbers) { if (int.TryParse(mt, out int n) && n > mtMaxSeq) mtMaxSeq = n; }

        int nextSeq = globalMaxSeq + 1; int nextMtSeq = mtMaxSeq + 1; int totalAssigned = 0; int totalMtAssigned = 0;
        string groupNumber = group.GroupNumber;

        foreach (var config in configs)
        {
            var studentIds = await AccessService.GetAllowedStudentIdsAsync(_context, groupId, config.CourseId);
            if (!studentIds.Any()) continue;

            var sortedStudents = await _context.Users.Where(u => studentIds.Contains(u.Id)).OrderBy(u => u.LastName).ThenBy(u => u.FirstName).ThenBy(u => u.MiddleName).ToListAsync();
            bool isMintrudCourse = config.Course != null && config.Course.MintrudProgramId != null;

            foreach (var student in sortedStudents)
            {
                var progress = await _context.CourseProgresses.FirstOrDefaultAsync(p => p.CourseId == config.CourseId && p.StudentId == student.Id && p.GroupId == groupId);
                if (progress == null)
                {
                    progress = new CourseProgress { CourseId = config.CourseId, StudentId = student.Id, GroupId = groupId };
                    _context.CourseProgresses.Add(progress);
                }

                if (isMintrudCourse && string.IsNullOrEmpty(progress.MintrudNumber)) { progress.MintrudNumber = nextMtSeq.ToString(); nextMtSeq++; totalMtAssigned++; }
                else if (!isMintrudCourse && string.IsNullOrEmpty(progress.CertificateNumber)) { progress.CertificateNumber = $"{nextSeq}-{groupNumber}"; nextSeq++; totalAssigned++; }
            }
        }

        await _context.SaveChangesAsync();
        if (totalAssigned == 0 && totalMtAssigned == 0) return Json(new { success = true, message = "У всех студентов уже есть номера." });

        string msg = ""; if (totalAssigned > 0) msg += $"Обычных: {totalAssigned}. "; if (totalMtAssigned > 0) msg += $"Минтруд: {totalMtAssigned}.";
        await _auditService.LogAsync("Update", "CourseProgress", groupId, $"Присвоены номера (Группа {group.GroupNumber}). Обычных: {totalAssigned}, Минтруд: {totalMtAssigned}");
        return Json(new { success = true, message = $"Успешно присвоено: {msg}" });
    }

    // 4. Попытки теста (для модалки)
    [HttpGet]
    public async Task<IActionResult> GetStudentTestAttempts(int studentId, int courseId, int groupId)
    {
        var attempts = await _context.TestAttempts.Include(a => a.CourseMaterial).Where(a => a.StudentId == studentId && a.CourseMaterial!.CourseId == courseId && a.GroupId == groupId).ToListAsync();
        var result = attempts.Select(a => new
        {
            a.Id,
            TestName = a.CourseMaterial!.Title,
            Status = a.Status.ToString(),
            a.ScorePercentage,
            a.CorrectAnswersCount,
            a.TotalQuestionsCount,
            FinishedAt = a.FinishedAt.HasValue ? a.FinishedAt.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : "Не завершен"
        }).ToList();
        return Json(result);
    }

    // 5. Экспорт Минтруд XML (Вызов сервиса)
    [HttpPost]
    public async Task<IActionResult> ExportGroupsToMintrudXml([FromForm] List<int> groupIds)
    {
        var result = await _exportService.ExportMintrudXmlAsync(groupIds);
        if (!result.Success) return BadRequest(new { success = false, message = result.Error });
        Response.Headers["Content-Disposition"] = $"attachment; filename=\"{Uri.EscapeDataString(result.FileName)}\"; filename*=UTF-8''{Uri.EscapeDataString(result.FileName)}";
        await _auditService.LogAsync("Export", "System", null, $"Выгрузка Минтруд (xml) для групп: {string.Join(", ", groupIds)}");
        return File(result.Data!, "application/xml");
    }

    // 6. Экспорт ФРДО (Вызов сервиса)
    [HttpPost]
    public async Task<IActionResult> ExportGroupsToFrdoExcel([FromForm] List<int> groupIds)
    {
        var result = await _exportService.ExportFrdoExcelAsync(groupIds);
        if (!result.Success) return BadRequest(new { success = false, message = result.Error });
        var cd = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileNameStar = result.FileName };
        Response.Headers[Microsoft.Net.Http.Headers.HeaderNames.ContentDisposition] = cd.ToString();
        await _auditService.LogAsync("Export", "System", null, $"Выгрузка ФРДО (Excel) для групп: {string.Join(", ", groupIds)}");
        return File(result.Data!, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    }

    // 7. Экспорт Статистики (Вызов сервиса)
    [HttpPost]
    public async Task<IActionResult> ExportGroupsStatistics([FromForm] List<int> groupIds)
    {
        var result = await _exportService.ExportStatisticsExcelAsync(groupIds);
        if (!result.Success) return BadRequest(new { success = false, message = result.Error });
        var cd = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileNameStar = result.FileName };
        Response.Headers[Microsoft.Net.Http.Headers.HeaderNames.ContentDisposition] = cd.ToString();
        await _auditService.LogAsync("Export", "System", null, $"Выгрузка статистики для групп: {string.Join(", ", groupIds)}");
        return File(result.Data!, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    }

    // 8. Импорт из Минтруда
    [HttpPost]
    public async Task<IActionResult> ImportMintrudNumbers(IFormFile excelFile)
    {
        if (excelFile == null || excelFile.Length == 0) return Json(new { success = false, message = "Файл не выбран." });
        using var stream = new MemoryStream(); await excelFile.CopyToAsync(stream); using var workbook = new XLWorkbook(stream);
        var worksheet = workbook.Worksheet(1); var headerRow = worksheet.Row(1);
        int colExternalId = headerRow.CellsUsed().First(c => c.GetString().Trim() == "Id внешней системы").Address.ColumnNumber;
        int colRegNumber = headerRow.CellsUsed().First(c => c.GetString().Trim() == "Номер в реестре").Address.ColumnNumber;

        int updated = 0, notFound = 0;
        foreach (var row in worksheet.RowsUsed().Skip(1))
        {
            string extId = row.Cell(colExternalId).GetString().Trim(); string regNum = row.Cell(colRegNumber).GetString().Trim();
            if (string.IsNullOrEmpty(extId) || string.IsNullOrEmpty(regNum)) continue;
            var progress = await _context.CourseProgresses.FirstOrDefaultAsync(p => p.MintrudNumber == extId);
            if (progress != null) { progress.MintrudOfficialNumber = regNum; updated++; } else { notFound++; }
        }
        await _auditService.LogAsync("Import", "System", null, $"Импорт официальных номеров Минтруда. Обновлено: {updated}");
        await _context.SaveChangesAsync();
        return Json(new { success = true, message = $"Обновлено: {updated}. Не найдено: {notFound}." });
    }

    // 9. Скачивание протокола теста (оставлен здесь из-за XceedDocX)
    [HttpGet]
    public async Task<IActionResult> DownloadTestResult(int attemptId)
    {
        var attempt = await _context.TestAttempts
            .Include(a => a.CourseMaterial).ThenInclude(m => m!.Course)
            .Include(a => a.Student).ThenInclude(u => u!.Company)
            .FirstOrDefaultAsync(a => a.Id == attemptId);
        if (attempt == null) return NotFound("Протокол не найден.");

        var studentAnswers = new Dictionary<int, List<int>>();
        if (!string.IsNullOrEmpty(attempt.StudentAnswersJson))
        {
            try { studentAnswers = System.Text.Json.JsonSerializer.Deserialize<Dictionary<int, List<int>>>(attempt.StudentAnswersJson) ?? new Dictionary<int, List<int>>(); } catch { }
        }

        var answeredQuestionIds = studentAnswers.Keys.ToList();
        var questions = await _context.TestQuestions.Include(q => q.Answers).Where(q => answeredQuestionIds.Contains(q.Id)).ToListAsync();
        var memoryStream = new MemoryStream();

        using (var doc = XceedDocX.Create(memoryStream))
        {
            doc.SetDefaultFont(new XceedFont("Times New Roman"), 12);
            var pHeader = doc.InsertParagraph("АВТОНОМНАЯ НЕКОММЕРЧЕСКАЯ ОРГАНИЗАЦИЯ ДОПОЛНИТЕЛЬНОГО\nПРОФЕССИОНАЛЬНОГО ОБРАЗОВАНИЯ \"ЭНЕРГЕТИК\"");
            pHeader.Alignment = XceedAlignment.center; pHeader.FontSize(10).Bold(); doc.InsertParagraph("\n");
            var pTitle = doc.InsertParagraph($"ИТОГОВЫЙ ЭКЗАМЕН № {attempt.Id}-{attempt.StudentId}");
            pTitle.Alignment = XceedAlignment.center; pTitle.FontSize(14).Bold();
            var pDate = doc.InsertParagraph($"«{attempt.StartedAt.ToLocalTime().ToString("dd")}» {attempt.StartedAt.ToLocalTime().ToString("MMMM yyyy")} г.");
            pDate.Alignment = XceedAlignment.right; pDate.FontSize(11).Italic(); doc.InsertParagraph("\n");

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

            var table = doc.AddTable(questions.Count + 1, 4);
            table.Alignment = XceedAlignment.center; table.Design = XceedTableDesign.TableGrid;
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

                studentAnswers.TryGetValue(q.Id, out var selectedOptionIds); selectedOptionIds ??= new List<int>();
                var answerCell = row.Cells[2]; var firstParagraph = answerCell.Paragraphs[0];
                if (firstParagraph.Text.Length > 0) firstParagraph.RemoveText(0, firstParagraph.Text.Length);

                if (!selectedOptionIds.Any()) firstParagraph.Append("Нет ответа").Italic();
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
            doc.InsertTable(table); doc.InsertParagraph("\n");

            int totalQ = attempt.TotalQuestionsCount > 0 ? attempt.TotalQuestionsCount : questions.Count;
            int err = totalQ - attempt.CorrectAnswersCount; if (err < 0) err = 0;
            int errPct = totalQ > 0 ? (int)Math.Round((double)err / totalQ * 100) : 0;
            int allowedErrPct = 100 - attempt.CourseMaterial!.PassPercentage;
            int allowedErrCount = (int)Math.Floor((double)totalQ * allowedErrPct / 100);

            doc.InsertParagraph($"Итого вопросов: {totalQ}");
            doc.InsertParagraph($"Допустимое количество ошибок: {allowedErrCount} ({allowedErrPct} %)");
            doc.InsertParagraph($"Допущено ошибок: {err} ({errPct} %)");
            doc.InsertParagraph($"Верных ответов: {attempt.CorrectAnswersCount} ({attempt.ScorePercentage} %)");
            doc.InsertParagraph("\n");

            var pFinalResult = doc.InsertParagraph("Результат тестирования: ");
            pFinalResult.Append(attempt.Status == AttemptStatus.Passed ? "СДАНО" : "НЕ СДАНO").Bold();
            doc.InsertParagraph("\n").Append("При проведении тестирования нарушений его порядка не зафиксировано.").FontSize(12);
            doc.InsertParagraph("\n\n");
            doc.InsertParagraph("Ответственный за проведение тестирования  _________________ / _________________").SpacingAfter(25);
            string shortName = $"{attempt.Student?.LastName} {attempt.Student?.FirstName.FirstOrDefault()}.{attempt.Student?.MiddleName?.FirstOrDefault()}.";
            doc.InsertParagraph($"Тестируемый  _________________ / {shortName}");
            doc.Save();
        }

        memoryStream.Position = 0;
        return File(memoryStream, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", $"Protocol_{attempt.Id}_{attempt.Student?.LastName}.docx");
    }

    private async Task<(string Name, string Position)> ResolveCommissionMemberAsync(string? input)
    {
        if (string.IsNullOrEmpty(input)) return ("—", "—");
        if (int.TryParse(input, out int id))
        {
            var member = await _context.CommissionMembers.FindAsync(id);
            if (member != null) return ($"{member.LastName} {member.FirstName} {member.MiddleName}".Trim(), member.Position);
        }
        if (input.Contains(',')) { var parts = input.Split(',', 2); return (parts[0].Trim(), parts[1].Trim()); }
        return (input.Trim(), "—");
    }
}