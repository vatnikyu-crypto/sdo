using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using MiniSoftware;
using Net.Codecrete.QrCodeGenerator;
using SdoApp.Models;
using System.Text.Json;

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

    // Главная страница: список групп -> курсы -> студенты
    public async Task<IActionResult> Index()
    {
        // Загружаем группы со всеми связями
        var groups = await _context.Groups
            .Include(g => g.GroupCompanies)
                .ThenInclude(gc => gc.Company)
            .ToListAsync();

        // Загружаем конфигурации курсов для групп
        var groupConfigs = await _context.GroupCourseConfigs
            .Include(c => c.Course)
                .ThenInclude(c => c!.DocumentTemplates)
            .ToListAsync();

        // Загружаем всех пользователей-слушателей для сопоставления по ID
        var students = await _context.Users
            .Where(u => u.Role == UserRole.Обучающийся)
            .ToDictionaryAsync(u => u.Id);

        var commission = await _context.CommissionMembers
            .OrderBy(c => c.LastName)
            .ToListAsync();

        ViewBag.GroupConfigs = groupConfigs;
        ViewBag.Students = students;
        ViewBag.Commission = commission;

        return View(groups);
    }

    // AJAX-метод для получения сданных тестов студента (для Push-окна)
    [HttpGet]
    public async Task<IActionResult> GetStudentTestAttempts(int studentId, int courseId)
    {
    // Загружаем попытки в память через ToListAsync, чтобы .NET мог применить ToLocalTime()
    var attempts = await _context.TestAttempts
        .Include(a => a.CourseMaterial)
        .Where(a => a.StudentId == studentId && a.CourseMaterial!.CourseId == courseId)
        .ToListAsync();

    var result = attempts.Select(a => new
    {
        a.Id,
        TestName = a.CourseMaterial!.Title,
        Status = a.Status.ToString(), // Теперь вернет "Passed", "Failed" и т.д.
        a.ScorePercentage,
        a.CorrectAnswersCount,
        a.TotalQuestionsCount,
        // Переводим UTC-время базы данных в локальное время вашего сервера
        FinishedAt = a.FinishedAt.HasValue 
            ? a.FinishedAt.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm") 
            : "Не завершен"
    }).ToList();

    return Json(result);
    }

    // 3. Кнопка "Сделать отчет" — Чтение шаблона .docx и автоподстановка переменных через MiniWord
    [HttpPost]
    public async Task<IActionResult> GenerateCourseReport(int groupConfigId, int templateId, string chairmanInput, string member1Input, string member2Input)
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

        // Парсим ID студентов, привязанных к курсу в группе
        var studentIds = config.AllowedStudentIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse)
            .ToList();

        var students = await _context.Users
            .Include(u => u.Company)
            .Where(u => studentIds.Contains(u.Id))
            .ToListAsync();

        var chairmanData = await ResolveCommissionMemberAsync(chairmanInput);
        var member1Data = await ResolveCommissionMemberAsync(member1Input);
        var member2Data = await ResolveCommissionMemberAsync(member2Input);

        try
        {
            // Передаем путь к wwwroot в наш обновленный сервис
            var documentService = new DocumentGenerationService(_env.WebRootPath);
            byte[] fileBytes = await documentService.GenerateDocxReportAsync(config, template, students, chairmanData, member1Data, member2Data);

            string fileName = $"{template.Name}_{config.Group?.GroupNumber ?? "Группа"}.docx";

            // Отдаем сгенерированный файл с корректным MIME-типом Word
            return File(fileBytes, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", fileName);
        }
        catch (FileNotFoundException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Внутренняя ошибка при генерации документа: {ex.Message}");
        }
    }

    private async Task<(string Name, string Position)> ResolveCommissionMemberAsync(string input)
    {
        if (string.IsNullOrEmpty(input)) return ("—", "—");

        // Сценарий 1: Если прилетел ID из базы данных (число)
        if (int.TryParse(input, out int id))
        {
            var member = await _context.CommissionMembers.FindAsync(id);
            if (member != null)
            {
                // Собираем полное ФИО для документа
                string fullName = $"{member.LastName} {member.FirstName} {member.MiddleName}".Trim();
                return (fullName, member.Position);
            }
        }

        // Сценарий 2: Если это ручной ввод и пользователь написал разделение через запятую
        if (input.Contains(','))
        {
            // Делим строку на 2 части по первой запятой
            var parts = input.Split(',', 2);
            return (parts[0].Trim(), parts[1].Trim());
        }

        // Сценарий 3: Если ввели просто текст без запятой (только ФИО)
        return (input.Trim(), "—");
    }

    // 4. Заглушка для скачивания конкретного результата теста из push-окна
    [HttpGet]
    public IActionResult DownloadTestResult(int attemptId)
    {
        byte[] fileBytes = System.Text.Encoding.UTF8.GetBytes($"Результат попытки тестирования #{attemptId}. (Заглушка)");
        return File(fileBytes, "text/plain", $"Test_Result_{attemptId}.txt");
    }
}

// ОБНОВЛЕННЫЙ СЕРВИС: Обрабатывает реальные файлы через MiniWord
public class DocumentGenerationService
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
            throw new FileNotFoundException("Путь к файлу шаблона не указан в базе данных.");
        }

        // Собираем точный физический путь к файлу на сервере
        string templateFullPath = Path.Combine(_webRootPath, template.FilePath.TrimStart('~', '/'));

        if (!File.Exists(templateFullPath))
        {
            throw new FileNotFoundException($"Файл шаблона не найден на сервере по пути: {template.FilePath}");
        }

        // Добавляем новые теги комиссии в словарь MiniWord
        var valueMap = new Dictionary<string, object>
    {
        { "NumberGroup", config.Group?.GroupNumber ?? "—" },
        { "GroupTitle", config.Group?.Title ?? "—" },
        { "CourseTitle", config.Course?.Title ?? "—" },
        { "ProtocolNumber", config.ProtocolNumber ?? "—" },
        { "StartDate", config.CourseStartDate?.ToString("dd.MM.yyyy") ?? config.Group?.StartDate.ToString("dd.MM.yyyy") ?? "—" },
        { "EndDate", config.CourseEndDate?.ToString("dd.MM.yyyy") ?? config.Group?.EndDate.ToString("dd.MM.yyyy") ?? "—" },
        { "CurrentDate", DateTime.Today.ToString("dd.MM.yyyy") },

        // Теги комиссии
        { "ChairmanName", chairman.Name },
        { "ChairmanPosition", chairman.Position },
        { "Member1Name", member1.Name },
        { "Member1Position", member1.Position },
        { "Member2Name", member2.Name },
        { "Member2Position", member2.Position }
    };

        // Безопасный блок генерации списка студентов с проверками на null
        var studentsList = students.Select((s, index) =>
        {
            var fullName = $"{s.LastName} {s.FirstName} {s.MiddleName}".Trim();

            // Ссылка проверки протокола
            string qrText = $"https://dpo-energo.ru/";

            // Генерируем QR-код
            var qr = QrCode.EncodeText(qrText, QrCode.Ecc.High);
            byte[] qrBytes = qr.ToPngBitmap();

            return new Dictionary<string, object>
            {
        { "i", index + 1 },
        { "FullName", fullName },
        // Защита: если позиция пустая, ставим прочерк
        { "Position", string.IsNullOrEmpty(s.Position) ? "—" : s.Position },
        // ЗАЩИТА ОТ NULL: проверяем, привязана ли компания к студенту
        { "Company", s.Company != null ? (s.Company.Name ?? "—") : "Частное лицо" },

        { "QrCode", new MiniWordPicture
    {
        Bytes = qrBytes,
        Extension = "PNG",          // Явно пишем null, чтобы движок MiniWord не спотыкался
        Width = 90,
        Height = 90
    }
}
            };
        }).ToList();


        // 3. ДОБАВЛЯЕМ ПОД КОРТКИМ КЛЮЧОМ "t"
        valueMap.Add("t", studentsList);

        using (var outputStream = new MemoryStream())
        {
            await MiniWord.SaveAsByTemplateAsync(outputStream, templateFullPath, valueMap);
            return outputStream.ToArray();
        }
    }
}
