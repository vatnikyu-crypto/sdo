using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models;
using MiniSoftware;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using System.Xml.Linq;
using ClosedXML.Excel;

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
        // ДОБАВИЛИ OrderByDescending(g => g.Id)
        var groups = await _context.Groups
            .Include(g => g.GroupCompanies)
                .ThenInclude(gc => gc.Company)
            .Include(g => g.GroupStudents)
            .OrderByDescending(g => g.Id) // Сортировка по ID (от большего к меньшему)
            .ToListAsync();

        var groupConfigs = await _context.GroupCourseConfigs
            .Include(c => c.Course)
                .ThenInclude(c => c!.DocumentTemplates)
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

        // КЛЮЧ ТЕПЕРЬ ВКЛЮЧАЕТ GroupId
        var progressMap = progresses.ToDictionary(
            p => $"{p.StudentId}_{p.CourseId}_{p.GroupId ?? 0}",
            p => p.CertificateNumber);

        ViewBag.GroupConfigs = groupConfigs;
        ViewBag.Students = students;
        ViewBag.Commission = commission;
        ViewBag.ProgressMap = progressMap;

        // === НОВОЕ: Определяем, кто сдал итоговые тесты ===

        // 1. Находим все материалы, которые являются "Итоговыми тестами"
        var finalTestMaterials = await _context.CourseMaterials
            .Where(m => m.Type == MaterialType.Test && m.TestKind == TestStatus.Итоговый)
            .ToDictionaryAsync(m => m.Id, m => m.CourseId); // Ключ: MaterialId, Значение: CourseId

        // 2. Находим все успешные попытки для этих тестов
        var passedAttempts = await _context.TestAttempts
    .Where(a => a.Status == AttemptStatus.Passed && finalTestMaterials.Keys.Contains(a.CourseMaterialId))
    .Select(a => new { a.StudentId, a.GroupId, a.CourseMaterialId }) // <-- ДОБАВИТЬ GroupId
    .ToListAsync();

        // 3. Создаем HashSet для быстрой проверки на лету (ключ: "StudentId_CourseId")
        var passedTestsSet = new HashSet<string>();
        foreach (var attempt in passedAttempts)
        {
            if (finalTestMaterials.TryGetValue(attempt.CourseMaterialId, out int courseId))
            {
                passedTestsSet.Add($"{attempt.StudentId}_{courseId}_{attempt.GroupId}");
            }
        }

        // Передаем во View
        ViewBag.PassedFinalTests = passedTestsSet;

        return View(groups);
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

        // Проверка на Минтруд (игнорируем Высоту=8 и ОЗП=10)
        var allowedMintrudIds = new[] { 8, 10 };
        bool hasBlockingMintrudCourses = configs.Any(c =>
            c.Course != null &&
            c.Course.MintrudProgramId != null &&
            !allowedMintrudIds.Contains(c.Course.MintrudProgramId.Value));

        if (hasBlockingMintrudCourses)
        {
            return Json(new { success = false, message = "Присвоение внутренних номеров заблокировано! В группе есть курсы Минтруда (кроме Высоты и ОЗП), для них выдаются только государственные номера." });
        }

        // =========================================================
        // 1. Поиск максимального ОБЫЧНОГО номера (CertificateNumber)
        // =========================================================
        var allCertificates = await _context.CourseProgresses
            .Where(p => !string.IsNullOrEmpty(p.CertificateNumber) && p.CertificateNumber.Contains("-"))
            .Select(p => p.CertificateNumber)
            .ToListAsync();

        int globalMaxSeq = 0;
        foreach (var cert in allCertificates)
        {
            var parts = cert!.Split('-');
            if (parts.Length >= 2 && int.TryParse(parts[0], out int seq))
            {
                if (seq > globalMaxSeq) globalMaxSeq = seq;
            }
        }

        // =========================================================
        // 2. Поиск максимального номера МИНТРУДА (MintrudNumber, просто число)
        // =========================================================
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

            bool isMintrudCourse = config.Course != null && config.Course.MintrudProgramId != null;

            foreach (var studentId in studentIds)
            {
                var progress = await _context.CourseProgresses
                    .FirstOrDefaultAsync(p =>
                        p.CourseId == config.CourseId &&
                        p.StudentId == studentId &&
                        p.GroupId == groupId);

                if (progress == null)
                {
                    progress = new CourseProgress
                    {
                        CourseId = config.CourseId,
                        StudentId = studentId,
                        GroupId = groupId
                    };
                    _context.CourseProgresses.Add(progress);
                }

                if (isMintrudCourse)
                {
                    // Для курсов Минтруда — просто число в MintrudNumber
                    if (string.IsNullOrEmpty(progress.MintrudNumber))
                    {
                        progress.MintrudNumber = nextMtSeq.ToString();
                        nextMtSeq++;
                        totalMtAssigned++;
                    }
                }
                else
                {
                    // Для обычных — стандартный номер в CertificateNumber
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

    // 2. AJAX-метод для получения сданных тестов студента (для Push-окна)
    [HttpGet]
    public async Task<IActionResult> GetStudentTestAttempts(int studentId, int courseId, int groupId)
    {
        var attempts = await _context.TestAttempts
        .Include(a => a.CourseMaterial)
        .Where(a => a.StudentId == studentId 
                 && a.CourseMaterial!.CourseId == courseId 
                 && a.GroupId == groupId) // <-- ДОБАВИТЬ ПРОВЕРКУ ГРУППЫ
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

    // МЕТОД: Выгрузка ОБЩЕГО XML по нескольким группам
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

            if (group == null)
            {
                errors.Add($"Группа с ID {groupId} не найдена.");
                continue;
            }

            var configs = await _context.GroupCourseConfigs
                .Include(c => c.Course).ThenInclude(c => c!.MintrudProgram)
                .Where(c => c.GroupId == groupId)
                .ToListAsync();

            if (!configs.Any())
            {
                errors.Add($"В группе №{group.GroupNumber} нет привязанных курсов.");
                continue;
            }

            bool allMintrud = configs.All(c => c.Course != null && c.Course.MintrudProgramId != null);
            if (!allMintrud)
            {
                errors.Add($"В группе №{group.GroupNumber} есть курсы, не относящиеся к программам Минтруда.");
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
                    .Include(p => p.Student).ThenInclude(s => s!.Company)
                    .Where(p => p.CourseId == config.CourseId && p.GroupId == groupId && studentIds.Contains(p.StudentId))
                    .ToListAsync();

                foreach (var p in progresses)
                {
                    allRecords.Add((config, p, group));
                }
            }
        }

        if (errors.Any())
            return BadRequest(new { success = false, message = string.Join(" ", errors) });

        if (!allRecords.Any())
            return BadRequest(new { success = false, message = "Нет записей для формирования XML (в выбранных группах нет студентов или прогресса)." });

        if (allRecords.Any(r => string.IsNullOrEmpty(r.progress.MintrudNumber)))
            return BadRequest(new { success = false, message = "У некоторых студентов нет присвоенного номера Минтруда. Сначала сохраните костяк групп или присвойте номера." });

        // ИСПРАВЛЕНО: используем .Select() вместо from..select
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

    // 4. Вспомогательный метод: Склейка двух документов
    private byte[] AppendDocxBytes(byte[] baseDocBytes, byte[] appendDocBytes)
    {
        using (var baseStream = new MemoryStream())
        {
            baseStream.Write(baseDocBytes, 0, baseDocBytes.Length);
            using (var appendStream = new MemoryStream(appendDocBytes))
            {
                using (var mainDocument = WordprocessingDocument.Open(baseStream, true))
                {
                    var mainPart = mainDocument.MainDocumentPart;
                    if (mainPart == null || mainPart.Document?.Body == null) return baseDocBytes;

                    mainPart.Document.Body.AppendChild(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
                    string altChunkId = "AltChunkId_" + Guid.NewGuid().ToString("N");
                    var chunk = mainPart.AddAlternativeFormatImportPart(AlternativeFormatImportPartType.WordprocessingML, altChunkId);
                    chunk.FeedData(appendStream);
                    mainPart.Document.Body.AppendChild(new AltChunk { Id = altChunkId });
                    mainPart.Document.Save();
                }
            }
            return baseStream.ToArray();
        }
    }

    // POST: /Report/ExportGroupsToFrdoExcel
    [HttpPost]
    public async Task<IActionResult> ExportGroupsToFrdoExcel([FromForm] List<int> groupIds)
    {
        if (groupIds == null || !groupIds.Any())
            return BadRequest(new { success = false, message = "Не выбрано ни одной группы." });

        var errors = new List<string>();
        var studentsToExport = new List<(User student, SdoApp.Models.Group group, Course course, GroupCourseConfig config, CourseProgress progress)>();

        var allowedMintrudIds = new[] { 8, 10 }; // Высота и ОЗП

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

            // ПРОВЕРКА: Не должно быть курсов Минтруда (кроме 8 и 10)
            bool hasBlockingMintrud = configs.Any(c =>
                c.Course != null &&
                c.Course.MintrudProgramId != null &&
                !allowedMintrudIds.Contains(c.Course.MintrudProgramId.Value));

            if (hasBlockingMintrud)
            {
                errors.Add($"В группе №{group.GroupNumber} есть курсы Минтруда, не относящиеся к ОЗП или Высоте. Выгрузка невозможна.");
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
                    // Проверка: должен быть внутренний номер
                    if (string.IsNullOrEmpty(p.CertificateNumber))
                    {
                        errors.Add($"У студента {p.Student?.LastName} {p.Student?.FirstName} нет присвоенного номера удостоверения. Сначала присвойте номера.");
                        continue;
                    }

                    studentsToExport.Add((p.Student!, group, config.Course!, config, p));
                }
            }
        }

        if (errors.Any())
            return BadRequest(new { success = false, message = string.Join(" ", errors) });

        if (!studentsToExport.Any())
            return BadRequest(new { success = false, message = "Нет данных для выгрузки." });

        // === ЗАПОЛНЕНИЕ EXCEL ===
        string templatePath = Path.Combine(_env.WebRootPath, "fis-frdo.xlsx");
        if (!System.IO.File.Exists(templatePath))
            return BadRequest(new { success = false, message = "Файл шаблона fis-frdo.xlsx не найден в корне проекта." });

        using var workbook = new XLWorkbook(templatePath);
        var worksheet = workbook.Worksheet(1); // Берем первый лист "Шаблон"

        int row = 2; // Начинаем со второй строки (первая - заголовки)

        foreach (var item in studentsToExport)
        {
            var student = item.student;
            var group = item.group;
            var course = item.course;
            var progress = item.progress;
            var config = item.config;

            // Вычисляем даты
            var endDate = config.CourseEndDate ?? group.EndDate;
            var startDate = config.CourseStartDate ?? group.StartDate;

            // А: Вид документа
            worksheet.Cell(row, 1).Value = "Удостоверение о повышении квалификации";
            // B: Статус документа
            worksheet.Cell(row, 2).Value = "Оригинал";
            // C, D, E: Подтверждения
            worksheet.Cell(row, 3).Value = "Нет";
            worksheet.Cell(row, 4).Value = "Нет";
            worksheet.Cell(row, 5).Value = "Нет";
            // F: Серия документа
            worksheet.Cell(row, 6).Value = "нет";
            // G: Номер документа
            worksheet.Cell(row, 7).Value = progress.CertificateNumber;
            // H: Дата выдачи документа
            worksheet.Cell(row, 8).Value = endDate.ToString("dd.MM.yyyy");
            // I: Регистрационный номер
            worksheet.Cell(row, 9).Value = progress.CertificateNumber;
            // J: Тип программы
            worksheet.Cell(row, 10).Value = "Повышение квалификации";
            // K: Наименование программы
            worksheet.Cell(row, 11).Value = course.Title;

            worksheet.Cell(row, 14).Value = "нет";

            // L - S (12-19) оставляем пустыми

            // T (19): Год начала
            worksheet.Cell(row, 19).Value = startDate.Year;
            // U (20): Год окончания
            worksheet.Cell(row, 20).Value = endDate.Year;
            // V (21): Срок обучения
            if (course.DurationHours.HasValue)
                worksheet.Cell(row, 21).Value = course.DurationHours.Value;

            // W (22): Фамилия
            worksheet.Cell(row, 22).Value = student.LastName;
            // X (23): Имя
            worksheet.Cell(row, 23).Value = student.FirstName;
            // Y (24): Отчество
            worksheet.Cell(row, 24).Value = student.MiddleName ?? "";
            // Z (25): Дата рождения
            if (student.BirthDate.HasValue)
                worksheet.Cell(row, 25).Value = student.BirthDate.Value.ToString("dd.MM.yyyy");

            // AA (26): Пол
            worksheet.Cell(row, 26).Value = student.Gender == Gender.Муж ? "Муж" : "Жен";

            // AB (27): СНИЛС
            worksheet.Cell(row, 27).Value = student.Snils ?? "";
            // AC (28): Форма обучения
            worksheet.Cell(row, 28).Value = "Очная";
            // AD (29): Источник финансирования
            worksheet.Cell(row, 29).Value = "Платное обучение";
            // AE (30): Форма получения образования
            worksheet.Cell(row, 30).Value = "в образовательной организации";
            // AF (31): Гражданство (643 - Россия)
            worksheet.Cell(row, 31).Value = "643";

            row++;
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        // Имя файла на русском
        string fileName = $"Отчет_ФРДО_{DateTime.Today:yyyy-MM-dd}.xlsx";

        // Используем стандартный заголовок с параметром filename* (RFC 5987)
        // Это гарантирует, что браузер возьмёт именно русское имя
        var contentDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
        {
            FileNameStar = fileName
        };
        Response.Headers[Microsoft.Net.Http.Headers.HeaderNames.ContentDisposition] = contentDisposition.ToString();

        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    }

    // POST: /Report/ExportGroupsStatistics
    [HttpPost]
    public async Task<IActionResult> ExportGroupsStatistics([FromForm] List<int> groupIds)
    {
        if (groupIds == null || !groupIds.Any())
            return BadRequest(new { success = false, message = "Не выбрано ни одной группы." });

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Статистика");

        // Заголовки
        string[] headers = {
            "Курс", "Группа", "Сроки обучения", "Слушатель",
            "Компания", "Подразделение", "Должность", "Дата сдачи итогового теста"
        };

        for (int i = 0; i < headers.Length; i++)
        {
            var cell = worksheet.Cell(1, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F0F4FF"); // Легкий синий фон
            cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        }

        int row = 2;
        bool hasData = false;

        foreach (var groupId in groupIds)
        {
            var group = await _context.Groups.FindAsync(groupId);
            if (group == null) continue;

            var configs = await _context.GroupCourseConfigs
                .Include(c => c.Course)
                .Where(c => c.GroupId == groupId)
                .ToListAsync();

            if (!configs.Any()) continue;

            foreach (var config in configs)
            {
                // Получаем список студентов
                var studentIds = config.AllowedStudentIds?
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(int.Parse).ToList() ?? new List<int>();

                if (!studentIds.Any())
                {
                    studentIds = await _context.GroupStudents
                        .Where(gs => gs.GroupId == groupId)
                        .Select(gs => gs.StudentId)
                        .ToListAsync();
                }

                var students = await _context.Users
                    .Include(u => u.Company)
                    .Where(u => studentIds.Contains(u.Id))
                    .OrderBy(u => u.LastName)
                    .ToListAsync();

                // Ищем материал "Итоговый тест" для этого курса
                var finalTest = await _context.CourseMaterials
                    .FirstOrDefaultAsync(m => m.CourseId == config.CourseId
                                           && m.Type == MaterialType.Test
                                           && m.TestKind == TestStatus.Итоговый);

                var startDateStr = config.CourseStartDate?.ToString("dd.MM.yyyy") ?? group.StartDate.ToString("dd.MM.yyyy");
                var endDateStr = config.CourseEndDate?.ToString("dd.MM.yyyy") ?? group.EndDate.ToString("dd.MM.yyyy");
                var datesRange = $"{startDateStr} - {endDateStr}";

                foreach (var student in students)
                {
                    hasData = true;
                    string testDateStr = "";

                    if (finalTest != null)
                    {
                        // Ищем ПЕРВУЮ успешную попытку (AttemptStatus.Passed — это единственный "успешный" статус в твоём enum)
                        var firstSuccessAttempt = await _context.TestAttempts
                            .Where(a => a.CourseMaterialId == finalTest.Id
                                     && a.StudentId == student.Id
                                     && a.Status == AttemptStatus.Passed)
                            .OrderBy(a => a.FinishedAt) // Самая ранняя успешная сдача
                            .FirstOrDefaultAsync();

                        if (firstSuccessAttempt != null && firstSuccessAttempt.FinishedAt.HasValue)
                        {
                            testDateStr = firstSuccessAttempt.FinishedAt.Value.ToString("dd.MM.yyyy");
                        }
                    }

                    // Заполняем строку
                    worksheet.Cell(row, 1).Value = config.Course?.Title ?? "Без названия";
                    worksheet.Cell(row, 2).Value = group.GroupNumber;
                    worksheet.Cell(row, 3).Value = datesRange;
                    worksheet.Cell(row, 4).Value = $"{student.LastName} {student.FirstName} {student.MiddleName}".Trim();
                    worksheet.Cell(row, 5).Value = student.Company?.Name ?? "Частное лицо";
                    worksheet.Cell(row, 6).Value = student.Department ?? "—";
                    worksheet.Cell(row, 7).Value = student.Position ?? "—";
                    worksheet.Cell(row, 8).Value = testDateStr;

                    // Границы для строки
                    worksheet.Range(row, 1, row, 8).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    worksheet.Range(row, 1, row, 8).Style.Border.InsideBorder = XLBorderStyleValues.Thin;

                    row++;
                }
            }
        }

        if (!hasData)
            return BadRequest(new { success = false, message = "Нет данных для выгрузки статистики." });

        // Автоширина
        worksheet.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        string fileName = $"Статистика_по_группам_{DateTime.Today:yyyy-MM-dd}.xlsx";

        // Настройка заголовка для скачивания (кириллица)
        var contentDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
        {
            FileNameStar = fileName
        };
        Response.Headers[Microsoft.Net.Http.Headers.HeaderNames.ContentDisposition] = contentDisposition.ToString();

        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    }

    // 5. Парсинг комиссии
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

    // 6. Скачивание результата теста
    [HttpGet]
    public IActionResult DownloadTestResult(int attemptId)
    {
        byte[] fileBytes = System.Text.Encoding.UTF8.GetBytes($"Результат попытки тестирования #{attemptId}. (Заглушка)");
        return File(fileBytes, "text/plain", $"Test_Result_{attemptId}.txt");
    }

    // POST: /Report/ImportMintrudNumbers
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
            var worksheet = workbook.Worksheet(1); // Берем первый лист

            // Ищем номера колонок по заголовкам (чтобы не зависеть от порядка колонок)
            var headerRow = worksheet.Row(1);
            int colExternalId = headerRow.CellsUsed().First(c => c.GetString().Trim() == "Id внешней системы").Address.ColumnNumber;
            int colRegNumber = headerRow.CellsUsed().First(c => c.GetString().Trim() == "Номер в реестре").Address.ColumnNumber;

            int updatedCount = 0;
            int notFoundCount = 0;

            // Проходим по всем строкам, начиная со 2-й
            foreach (var row in worksheet.RowsUsed().Skip(1))
            {
                string externalId = row.Cell(colExternalId).GetString().Trim();
                string regNumber = row.Cell(colRegNumber).GetString().Trim();

                if (string.IsNullOrEmpty(externalId) || string.IsNullOrEmpty(regNumber))
                    continue;

                // Ищем запись в базе по MintrudNumber
                var progress = await _context.CourseProgresses
                    .FirstOrDefaultAsync(p => p.MintrudNumber == externalId);

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
            if (notFoundCount > 0)
                message += $" Не найдено в системе (пропущено): {notFoundCount}.";

            return Json(new { success = true, message = message });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Ошибка при чтении файла: {ex.Message}" });
        }
    }
}

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
    List<User> students,
    (string Name, string Position) chairman,
    (string Name, string Position) member1,
    (string Name, string Position) member2,
    string? commissionOrderNumber,
    string? commissionOrderDate)
    {
        if (string.IsNullOrEmpty(template.FilePath))
        {
            throw new FileNotFoundException("Путь к файлу шаблона не указан.");
        }

        string templateFullPath = Path.Combine(_webRootPath, template.FilePath.TrimStart('~', '/'));

        if (!File.Exists(templateFullPath))
        {
            throw new FileNotFoundException($"Файл шаблона не найден.");
        }

        // 1. Одиночные переменные для шапки документа
        var valueMap = new Dictionary<string, object>
    {
        { "NumberGroup", config.Group?.GroupNumber ?? "—" },
        { "GroupTitle", config.Group?.Title ?? "—" },
        { "CourseTitle", config.Course?.Title ?? "—" },
        { "ProtocolNumber", config.ProtocolNumber ?? "—" },
        { "StartDate", config.CourseStartDate?.ToString("dd.MM.yyyy") ?? config.Group?.StartDate.ToString("dd.MM.yyyy") ?? "—" },
        { "EndDate", config.CourseEndDate?.ToString("dd.MM.yyyy") ?? config.Group?.EndDate.ToString("dd.MM.yyyy") ?? "—" },
        { "CurrentDate", DateTime.Today.ToString("dd.MM.yyyy") },
        { "ChairmanName", chairman.Name },
        { "ChairmanPosition", chairman.Position },
        { "Member1Name", member1.Name },
        { "Member1Position", member1.Position },
        { "Member2Name", member2.Name },
        { "Member2Position", member2.Position },
        { "CommissionOrderNumber", commissionOrderNumber ?? "—" },
        { "CommissionOrderDate", !string.IsNullOrEmpty(commissionOrderDate) ? DateTime.Parse(commissionOrderDate).ToString("dd.MM.yyyy") : "—" }
    };

        // =========================================================================
        // МАГИЯ ДЛЯ ДИПЛОМОВ (БЕЗ ТАБЛИЦ):
        // Если передан ровно один студент, делаем его данные ПЛОСКИМИ одиночными тегами
        // =========================================================================
        if (students.Count == 1)
        {
            var s = students[0];
            string fullName = $"{s.LastName} {s.FirstName} {s.MiddleName}".Trim();

            // Генерация QR для этого конкретного человека
            string qrText = $"https://dpo-energo.ru";
            var qr = Net.Codecrete.QrCodeGenerator.QrCode.EncodeText(qrText, Net.Codecrete.QrCodeGenerator.QrCode.Ecc.High);
            byte[] qrBytes = qr.ToPngBitmap();

            // Добавляем чистые теги БЕЗ префикса "t." прямо в корень словаря!
            valueMap.Add("i", 1);
            valueMap.Add("FullName", fullName);
            valueMap.Add("Position", string.IsNullOrEmpty(s.Position) ? "—" : s.Position);
            valueMap.Add("Company", s.Company != null ? (s.Company.Name ?? "—") : "Частное лицо");
            valueMap.Add("QrCode", new MiniWordPicture { Bytes = qrBytes, Extension = "PNG", Width = 90, Height = 90 });
        }
        else
        {
            // Если студентов много (это обычный протокол с таблицей), собираем старую добрую таблицу под ключом "t"
            var studentsList = students.Select((s, index) =>
            {
                var fullName = $"{s.LastName} {s.FirstName} {s.MiddleName}".Trim();
                string qrText = $"https://dpo-energo.ru";
                var qr = Net.Codecrete.QrCodeGenerator.QrCode.EncodeText(qrText, Net.Codecrete.QrCodeGenerator.QrCode.Ecc.High);
                byte[] qrBytes = qr.ToPngBitmap();

                return new Dictionary<string, object>
                {
                { "i", index + 1 },
                { "FullName", fullName },
                { "Position", string.IsNullOrEmpty(s.Position) ? "—" : s.Position },
                { "Company", s.Company != null ? (s.Company.Name ?? "—") : "Частное лицо" },
                { "QrCode", new MiniWordPicture { Bytes = qrBytes, Extension = "PNG", Width = 90, Height = 90 } }
                };
            }).ToList();

            valueMap.Add("t", studentsList);
        }

        using (var outputStream = new MemoryStream())
        {
            await MiniWord.SaveAsByTemplateAsync(outputStream, templateFullPath, valueMap);
            return outputStream.ToArray();
        }
    }


}