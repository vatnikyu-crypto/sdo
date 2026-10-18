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

namespace SdoApp.Controllers;

public class ReportController : Controller
{
    private readonly AppDbContext _context;
    private readonly IWebHostEnvironment _env;

    public ReportController(AppDbContext context, IWebHostEnvironment env)
    {
        _context = context;
        _env = env;
    }

    // [Переходим к Части 2...]
    // 1. Главная страница: список групп -> курсы -> студенты
    public async Task<IActionResult> Index()
    {
        var groups = await _context.Groups
            .Include(g => g.GroupCompanies)
                .ThenInclude(gc => gc.Company)
            .ToListAsync();

        var groupConfigs = await _context.GroupCourseConfigs
            .Include(c => c.Course)
                .ThenInclude(c => c!.DocumentTemplates)
            .ToListAsync();

        var students = await _context.Users
            .Where(u => u.Role == UserRole.Обучающийся)
            .ToDictionaryAsync(u => u.Id);

        // Загружаем список всех членов комиссии для выпадающих списков
        var commission = await _context.CommissionMembers
            .OrderBy(c => c.LastName)
            .ToListAsync();

        ViewBag.GroupConfigs = groupConfigs;
        ViewBag.Students = students;
        ViewBag.Commission = commission;

        return View(groups);
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
            Status = a.Status.ToString(), // Передает "Passed", "Failed" и т.д.
            a.ScorePercentage,
            a.CorrectAnswersCount,
            a.TotalQuestionsCount,
            FinishedAt = a.FinishedAt.HasValue
                ? a.FinishedAt.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
                : "Не завершен"
        }).ToList();

        return Json(result);
    }

    // [Переходим к Части 3...]
    // 3. Кнопка "Сформировать" — Обработка параметров генерации отчета
    [HttpPost]
    public async Task<IActionResult> GenerateCourseReport(
        int groupConfigId,
        int templateId,
        string? chairmanInput,
        string? member1Input,
        string? member2Input,
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

        var students = await _context.Users
            .Include(u => u.Company)
            .Where(u => studentIds.Contains(u.Id))
            .ToListAsync();

        var chairmanData = await ResolveCommissionMemberAsync(chairmanInput);
        var member1Data = await ResolveCommissionMemberAsync(member1Input);
        var member2Data = await ResolveCommissionMemberAsync(member2Input);

        var documentService = new DocumentGenerationService(_env.WebRootPath);
        string groupNumber = config.Group?.GroupNumber ?? "Группа";

        // =========================================================================
        // РЕЖИМ А: Индивидуальные документы (IsPerStudent) — Склеиваем всё в ОДИН файл .docx
        // =========================================================================
        if (template.IsPerStudent)
        {
            byte[]? finalDocBytes = null;

            foreach (var student in students)
            {
                var singleStudentList = new List<User> { student };
                byte[] currentDocBytes = await documentService.GenerateDocxReportAsync(
                    config, template, singleStudentList, chairmanData, member1Data, member2Data);

                if (finalDocBytes == null)
                {
                    finalDocBytes = currentDocBytes;
                }
                else
                {
                    finalDocBytes = AppendDocxBytes(finalDocBytes, currentDocBytes);
                }
            }

            if (finalDocBytes != null)
            {
                return File(finalDocBytes, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", $"{template.Name}_{groupNumber}_Все_Слушатели.docx");
            }
            return BadRequest("Список студентов пуст.");
        }

        // =========================================================================
        // РЕЖИМ Б: Разделение по компаниям (splitByCompany) — Упаковываем в ZIP
        // =========================================================================
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
                            config, template, group.ToList(), chairmanData, member1Data, member2Data);

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

        // =========================================================================
        // РЕЖИМ В: Обычный отчёт — Один файл со всеми студентами группы
        // =========================================================================
        try
        {
            byte[] fileBytes = await documentService.GenerateDocxReportAsync(
                config, template, students, chairmanData, member1Data, member2Data);

            return File(fileBytes, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", $"{template.Name}_{groupNumber}.docx");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Ошибка генерации: {ex.Message}");
        }
    }

    // [Переходим к Части 4...]
    // 4. Вспомогательный метод: Склейка двух документов OpenXML в один массив байт
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

                    // Добавляем разрыв страницы (Page Break) в конец базового документа
                    mainPart.Document.Body.AppendChild(new Paragraph(new Run(new Break { Type = BreakValues.Page })));

                    // Регистрируем уникальный идентификатор для нового куска текста
                    string altChunkId = "AltChunkId_" + Guid.NewGuid().ToString("N");

                    // Встраиваем бинарный поток как альтернативный фрагмент разметки
                    var chunk = mainPart.AddAlternativeFormatImportPart(AlternativeFormatImportPartType.WordprocessingML, altChunkId);
                    chunk.FeedData(appendStream);

                    // Добавляем altChunk элемент в дерево документа
                    mainPart.Document.Body.AppendChild(new AltChunk { Id = altChunkId });
                    mainPart.Document.Save();
                }
            }
            return baseStream.ToArray();
        }
    }

    // 5. Вспомогательный метод: Парсинг выбора Tom Select (База данных vs Ручной ввод)
    private async Task<(string Name, string Position)> ResolveCommissionMemberAsync(string? input)
    {
        if (string.IsNullOrEmpty(input)) return ("—", "—");

        // Если прилетел ID из базы данных (число)
        if (int.TryParse(input, out int id))
        {
            var member = await _context.CommissionMembers.FindAsync(id);
            if (member != null)
            {
                return ($"{member.LastName} {member.FirstName} {member.MiddleName}".Trim(), member.Position);
            }
        }

        // Если это ручной ввод текста и пользователь написал "ФИО, Должность"
        if (input.Contains(','))
        {
            var parts = input.Split(',', 2);
            return (parts[0].Trim(), parts[1].Trim());
        }

        return (input.Trim(), "—");
    }

    // 6. Заглушка для скачивания конкретного результата теста из push-окна
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
    (string Name, string Position) member2)
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
        { "Member2Position", member2.Position }
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