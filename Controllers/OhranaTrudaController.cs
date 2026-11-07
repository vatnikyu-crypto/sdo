using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models;
using SdoApp.Models.ViewModels;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace SdoApp.Controllers
{
    [AllowAnonymous] // Доступ для всех
    [Route("ohranatruda")]
    public class OhranaTrudaController : Controller
    {
        private readonly AppDbContext _context;

        public OhranaTrudaController(AppDbContext context)
        {
            _context = context;
        }

                // GET: /ohranatruda/123
        [HttpGet("{registryNumber}")]
        public async Task<IActionResult> Index(string registryNumber)
        {
            if (string.IsNullOrWhiteSpace(registryNumber))
                return View("NotFound");

            // Ищем прогресс по номеру Минтруда (обязательно подтягиваем Group)
            var progress = await _context.CourseProgresses
                .Include(p => p.Student).ThenInclude(s => s!.Company)
                .Include(p => p.Course).ThenInclude(c => c!.MintrudProgram)
                .Include(p => p.Group)
                .FirstOrDefaultAsync(p => p.MintrudOfficialNumber == registryNumber);

            if (progress == null || progress.Student == null || progress.Course == null)
                return View("NotFound");

            // Ищем настройки группы для курса (чтобы взять протокол и даты)
            var config = await _context.GroupCourseConfigs
                .FirstOrDefaultAsync(c => c.CourseId == progress.CourseId && c.GroupId == progress.GroupId);

            // Ищем итоговый тест курса
            var finalTest = await _context.CourseMaterials
                .FirstOrDefaultAsync(m => m.CourseId == progress.CourseId 
                                       && m.Type == MaterialType.Test 
                                       && m.TestKind == TestStatus.Итоговый);

            // Проверяем, сдал ли он его (хоть раз успешно)
            bool isPassed = false;
            if (finalTest != null)
            {
                isPassed = await _context.TestAttempts
                    .AnyAsync(a => a.StudentId == progress.StudentId 
                                && a.CourseMaterialId == finalTest.Id 
                                && a.GroupId == progress.GroupId 
                                && a.Status == AttemptStatus.Passed);
            }

            // Формируем ФИО (Маскировка)
            var user = progress.Student;
            string maskedLastName = MaskLastName(user.LastName);
            string initials = $"{GetInitial(user.FirstName)}{GetInitial(user.MiddleName)}".Trim();
            
            // Даты (берем из конфига курса, если нет — из группы)
            DateTime? courseEndDate = config?.CourseEndDate ?? progress.Group?.EndDate;
            DateTime? nextCheckDate = null;
            
            if (progress.Course.RecertificationMonths.HasValue && progress.Course.RecertificationMonths.Value > 0 && courseEndDate.HasValue)
            {
                nextCheckDate = courseEndDate.Value.AddMonths(progress.Course.RecertificationMonths.Value);
            }

            // Формируем модель для View
            var vm = new CertificateViewModel
            {
                MaskedFullName = $"{maskedLastName} {initials}".Trim(),
                CompanyName = user.Company?.Name ?? "Не указана",
                CompanyInn = user.Company?.Inn ?? "—",
                // Берем название программы Минтруда, если нет — обычное название курса
                ProgramName = progress.Course.MintrudProgram?.Name ?? progress.Course.Title ?? "—",
                MintrudOfficialNumber = progress.MintrudOfficialNumber,
                ProtocolNumber = !string.IsNullOrWhiteSpace(config?.ProtocolNumber) ? config.ProtocolNumber : "—",
                ProtocolDate = courseEndDate,
                NextCheckDate = nextCheckDate,
                IsPassed = isPassed
            };

            return View(vm);
        }

        // Логика маскировки фамилии (Л*, К*ц, К**ш, Ив**ов, Ан***ев)
        private string MaskLastName(string lastName)
        {
            if (string.IsNullOrWhiteSpace(lastName)) return "";
            lastName = lastName.Trim();
            int len = lastName.Length;

            if (len == 1) return "*";
            if (len == 2) return $"{lastName[0]}*";
            if (len == 3) return $"{lastName[0]}*{lastName[2]}";
            if (len == 4) return $"{lastName[0]}**{lastName[3]}";
            
            // Для 5 и более букв: 2 первые + звездочки + 2 последние
            string start = lastName.Substring(0, 2);
            string end = lastName.Substring(len - 2, 2);
            string middle = new string('*', len - 4);
            return $"{start}{middle}{end}";
        }

        // Извлечение первой буквы для инициалов
        private string GetInitial(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            return $"{name.Trim()[0]}.";
        }
    }
}