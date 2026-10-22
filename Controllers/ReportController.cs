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
    public async Task<IActionResult> GetStudentTestAttempts(int studentId, int courseId)
    {
        var attempts = await _context.TestAttempts
            .Include(a => a.CourseMaterial)
            .Where(a => a.StudentId == studentId && a.CourseMaterial!.CourseId == courseId)
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

    // МЕТОД: Выгрузка ОБЩЕГО XML по всей группе (только если ВСЕ курсы Минтрудовские)
    [HttpGet]
    public async Task<IActionResult> ExportGroupToMintrudXml(int groupId)
    {
        // 1. Получаем группу со всеми курсами (ПЕРЕИМЕНОВАЛИ В currentGroup!)
        var currentGroup = await _context.Groups
            .Include(g => g.GroupStudents)
            .FirstOrDefaultAsync(g => g.Id == groupId);

        if (currentGroup == null)
            return BadRequest(new { success = false, message = "Группа не найдена." });

        var configs = await _context.GroupCourseConfigs
            .Include(c => c.Course).ThenInclude(c => c!.MintrudProgram)
            .Where(c => c.GroupId == groupId)
            .ToListAsync();

        if (!configs.Any())
            return BadRequest(new { success = false, message = "В группе нет привязанных курсов." });

        // 2. ПРОВЕРКА: ВСЕ курсы в группе должны быть Минтрудовскими
        bool allMintrud = configs.All(c => c.Course != null && c.Course.MintrudProgramId != null);
        if (!allMintrud)
        {
            return BadRequest(new { success = false, message = "Нельзя сформировать XML Минтруда! В группе есть курсы, не относящиеся к программам Минтруда. Шаблон доступен только для групп, где ВСЕ курсы являются программами Минтруда." });
        }

        // 3. Собираем все записи (студент × курс) для этой группы
        var allRecords = new List<(GroupCourseConfig Config, CourseProgress Progress)>();

        foreach (var cfg in configs)
        {
            var studentIds = cfg.AllowedStudentIds?
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(int.Parse).ToList() ?? new List<int>();

            if (!studentIds.Any() && currentGroup.GroupStudents != null)
            {
                studentIds = currentGroup.GroupStudents.Select(gs => gs.StudentId).ToList();
            }

            if (!studentIds.Any()) continue;

            var progresses = await _context.CourseProgresses
                .Include(p => p.Student).ThenInclude(s => s!.Company)
                .Where(p => p.CourseId == cfg.CourseId && p.GroupId == groupId && studentIds.Contains(p.StudentId))
                .ToListAsync();

            foreach (var p in progresses)
            {
                allRecords.Add((cfg, p));
            }
        }

        if (!allRecords.Any())
            return BadRequest(new { success = false, message = "Нет записей для формирования XML (в группе нет студентов или прогресса)." });

        if (allRecords.Any(r => string.IsNullOrEmpty(r.Progress.MintrudNumber)))
        {
            return BadRequest(new { success = false, message = "Ошибка: У некоторых студентов нет присвоенного номера Минтруда (МТ-). Пожалуйста, сначала сохраните костяк группы или обновите номера." });
        }

            // 4. Формируем XML
            var xDoc = new XDocument(
                new XElement("RegistrySet",
                    new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
                    from rec in allRecords
                    select new XElement("RegistryRecord",
                        new XAttribute("outerId", rec.Progress.MintrudNumber),
                        new XElement("Worker",
                            new XElement("LastName", rec.Progress.Student!.LastName ?? ""),
                            new XElement("FirstName", rec.Progress.Student.FirstName ?? ""),
                            new XElement("MiddleName", rec.Progress.Student.MiddleName ?? ""),
                            new XElement("Snils", rec.Progress.Student.Snils ?? ""),
                            new XElement("Citizenship", "Россия"),
                            new XElement("Position", rec.Progress.Student.Position ?? ""),
                            new XElement("EmployerInn", rec.Progress.Student.Company?.Inn ?? ""),
                            new XElement("EmployerTitle", rec.Progress.Student.Company?.Name ?? "")
                        ),
                        new XElement("Organization",
                            new XElement("Inn", "7017999506"),
                            new XElement("Title", "АНО ДПО \"ЭНЕРГЕТИК\"")
                        ),
                        new XElement("Test",
                            new XAttribute("isPassed", "true"),
                            new XAttribute("learnProgramId", rec.Config.Course!.MintrudProgram!.NumberProgram ?? ""),
                            new XElement("Date", rec.Config.CourseEndDate?.ToString("yyyy-MM-dd") ?? currentGroup.EndDate.ToString("yyyy-MM-dd")),
                            new XElement("ProtocolNumber", rec.Config.ProtocolNumber ?? ""),
                            new XElement("LearnProgramTitle", rec.Config.Course.MintrudProgram.Name ?? "")
                        )
                    )
                )
            );

            // 5. Отдаем файл
            using (var stream = new MemoryStream())
            {
                xDoc.Save(stream);
                stream.Position = 0;

                string fileName = $"EISOT_Group_{currentGroup.GroupNumber}.xml";
                return File(stream.ToArray(), "application/xml", fileName);
            }
        }

        // 3. Кнопка "Сформировать" — Обработка параметров генерации отчета
        [HttpPost]
        public async Task<IActionResult> GenerateCourseReport(
            int groupConfigId,
            int templateId,
            string? chairmanInput,
            string? member1Input,
            string? member2Input,
            string? commissionOrderNumber,
            string? commissionOrderDate,
            bool splitByCompany = false)
        {
            var config = await _context.GroupCourseConfigs
                .Include(c => c.Group)
                .Include(c => c.Course)
                .FirstOrDefaultAsync(c => c.Id == groupConfigId);

            var template = await _context.CourseDocumentTemplates
                .FirstOrDefaultAsync(t => t.Id == templateId);

            if (config == null || template == null)
            {
                return NotFound("Конфигурация курса или шаблон документа не найдены.");
            }

            var studentIds = config.AllowedStudentIds
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(int.Parse).ToList();

            // Если список пуст (не настраивали), берем всех из группы
            if (!studentIds.Any() && config.Group != null)
            {
                // Придется подгрузить GroupStudents, если их нет
                var groupStudents = await _context.GroupStudents
                    .Where(gs => gs.GroupId == config.GroupId)
                    .Select(gs => gs.StudentId)
                    .ToListAsync();
                studentIds = groupStudents;
            }

            var students = await _context.Users
                .Include(u => u.Company)
                .Where(u => studentIds.Contains(u.Id))
                .ToListAsync();

            var chairmanData = await ResolveCommissionMemberAsync(chairmanInput);
            var member1Data = await ResolveCommissionMemberAsync(member1Input);
            var member2Data = await ResolveCommissionMemberAsync(member2Input);

            var documentService = new DocumentGenerationService(_env.WebRootPath);
            string groupNumber = config.Group?.GroupNumber ?? "Группа";

            // РЕЖИМ А: Индивидуальные документы
            if (template.IsPerStudent)
            {
                byte[]? finalDocBytes = null;

                foreach (var student in students)
                {
                    var singleStudentList = new List<User> { student };
                    byte[] currentDocBytes = await documentService.GenerateDocxReportAsync(
                        config, template, singleStudentList, chairmanData, member1Data, member2Data, commissionOrderNumber, commissionOrderDate);

                    if (finalDocBytes == null) finalDocBytes = currentDocBytes;
                    else finalDocBytes = AppendDocxBytes(finalDocBytes, currentDocBytes);
                }

                if (finalDocBytes != null)
                {
                    return File(finalDocBytes, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", $"{template.Name}_{groupNumber}_Все_Слушатели.docx");
                }
                return BadRequest("Список студентов пуст.");
            }

            // РЕЖИМ Б: Разделение по компаниям
            if (splitByCompany)
            {
                var studentGroups = students.GroupBy(s => s.Company?.Name ?? "Физические лица").ToList();

                using (var zipMS = new MemoryStream())
                {
                    using (var archive = new ZipArchive(zipMS, ZipArchiveMode.Create, true))
                    {
                        foreach (var group in studentGroups)
                        {
                            byte[] docBytes = await documentService.GenerateDocxReportAsync(
                                config, template, group.ToList(), chairmanData, member1Data, member2Data, commissionOrderNumber, commissionOrderDate);

                            var zipEntry = archive.CreateEntry($"{template.Name}_{group.Key}.docx", CompressionLevel.Optimal);
                            using (var entryStream = zipEntry.Open())
                            {
                                await entryStream.WriteAsync(docBytes, 0, docBytes.Length);
                            }
                        }
                    }
                    return File(zipMS.ToArray(), "application/zip", $"{template.Name}_{groupNumber}_По_Компаниям.zip");
                }
            }

            // РЕЖИМ В: Обычный отчёт
            try
            {
                byte[] fileBytes = await documentService.GenerateDocxReportAsync(
                    config, template, students, chairmanData, member1Data, member2Data, commissionOrderNumber, commissionOrderDate);

                return File(fileBytes, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", $"{template.Name}_{groupNumber}.docx");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Ошибка генерации: {ex.Message}");
            }
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