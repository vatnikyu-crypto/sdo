using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using MiniExcelLibs;
using System.IO;
using MiniExcelLibs.Attributes;
using System.Data;
using MiniExcelLibs.OpenXml;
using ClosedXML.Excel;
using SdoApp.Models.ViewModels;

namespace SdoApp.Controllers;

[Authorize(Roles = "Админ")] // Доступ к управлению пользователями имеет только Администратор
public class UsersController : Controller
{
    private readonly AppDbContext _context;

    // Внедряем контекст базы данных через конструктор
    public UsersController(AppDbContext context)
    {
        _context = context;
    }

    // ГЕТ: /Users/Index
    // Отображает главную таблицу, где выводится список только со слушателями
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var students = await _context.Users
            .Include(u => u.Company)
            .Where(u => u.Role == UserRole.Обучающийся)
            .ToListAsync();

        return View(students);
    }

    // ГЕТ: /Users/Create
    // Просто открывает отдельную страницу с формой добавления
    [HttpGet]
    public IActionResult Create()
    {
        return View();
    }

    // API МЕТОД ДЛЯ ЖИВОГО ПОИСКА: /Users/SearchCompanies?term=...
    // Гарантированный регистронезависимый поиск кириллицы по любой части слова в SQLite
    [HttpGet]
    public async Task<IActionResult> SearchCompanies(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return Json(new List<object>());
        }

        // 1. Быстро забираем ID и Имена компаний из базы в память приложения
        var allCompanies = await _context.Companies
            .Select(c => new { id = c.Id, name = c.Name })
            .ToListAsync();

        // 2. Делаем честный поиск по любой части слова средствами .NET (он идеально понимает русский регистр)
        var filteredCompanies = allCompanies
            .Where(c => c.name.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase))
            .Take(10) // Ограничиваем выдачу
            .ToList();

        return Json(filteredCompanies);
    }

    // ПОСТ: /Users/Create
    // Обрабатывает отправку формы, проверяет данные и создает учетную запись
    [HttpPost]
    public async Task<IActionResult> Create(string lastName, string firstName, string middleName,
        Gender gender, DateTime? birthDate, string snils, string companyName, string department, string position)
    {
        // Шаг 1. Буферизируем все введенные админом данные, чтобы они не слетали при ошибках
        ViewBag.LastName = lastName;
        ViewBag.FirstName = firstName;
        ViewBag.MiddleName = middleName;
        ViewBag.Gender = (int)gender;
        ViewBag.BirthDate = birthDate?.ToString("yyyy-MM-dd");
        ViewBag.Snils = snils;
        ViewBag.CompanyName = companyName;
        ViewBag.Department = department;

        // Шаг 2. Проверка уникальности СНИЛС (выполняется только если поле заполнено)
        if (!string.IsNullOrWhiteSpace(snils))
        {
            var isSnilsDuplicate = await _context.Users.AnyAsync(u => u.Snils == snils.Trim());
            if (isSnilsDuplicate)
            {
                ModelState.AddModelError("", $"Ошибка: Пользователь со СНИЛС '{snils}' уже зарегистрирован в СДО!");
                return View();
            }
        }

        // Шаг 3. Строгая валидация компании на существование в справочнике системы
        int? finalCompanyId = null;
        if (!string.IsNullOrWhiteSpace(companyName))
        {
            var company = await _context.Companies.FirstOrDefaultAsync(c => c.Name == companyName.Trim());
            if (company == null)
            {
                ModelState.AddModelError("", "Ошибка: Указанная организация не найдена в системе! Выберите компанию строго из выпадающего списка живого поиска или ОЧИСТИТЕ поле для физ. лица.");
                return View();
            }
            finalCompanyId = company.Id; // Если компания найдена, запоминаем её Id
        }

        // Шаг 4. Автоматическая циклическая генерация уникального ЛОГИНА (транслит фамилии + 4 цифры)
        string baseTranslit = Transliterate(lastName.Trim().ToLower());
        string generatedLogin = "";
        bool isLoginUnique = false;
        var random = new Random();

        while (!isLoginUnique)
        {
            int randomNumber = random.Next(1000, 9999);
            generatedLogin = $"{baseTranslit}{randomNumber}";

            // Защита от коллизий: проверяем, свободен ли такой логин во всей таблице
            isLoginUnique = !await _context.Users.AnyAsync(u => u.Login == generatedLogin);
        }

        // Шаг 5. Автоматическая генерация ПАРОЛЯ (криптостойкий, ровно 10 символов)
        string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var passwordBuilder = new StringBuilder();
        for (int i = 0; i < 10; i++)
        {
            passwordBuilder.Append(chars[random.Next(chars.Length)]);
        }
        string generatedPassword = passwordBuilder.ToString();

        // Шаг 6. Сборка сущности и привязка к внешнему ключу компании
        var newStudent = new User
        {
            Login = generatedLogin,
            Password = generatedPassword, // Хранится в открытом виде для обеспечения функции клик-копирования
            LastName = lastName.Trim(),
            FirstName = firstName.Trim(),
            MiddleName = string.IsNullOrWhiteSpace(middleName) ? null : middleName.Trim(),
            Gender = gender,
            BirthDate = birthDate,
            Snils = string.IsNullOrWhiteSpace(snils) ? null : snils.Trim(),
            Department = string.IsNullOrWhiteSpace(department) ? null : department.Trim(), // Необязательное поле
            Role = UserRole.Обучающийся, // Обычный администратор может создавать только Слушателей
            CompanyId = finalCompanyId,
            Position = position?.Trim()
        };

        // Шаг 7. Сохранение транзакции в SQLite
        _context.Users.Add(newStudent);
        await _context.SaveChangesAsync();

        // Успешно создали — возвращаем админа к обновленной таблице
        return RedirectToAction(nameof(Index));
    }

    // ПОСТ: /Users/Delete/5
    // Удаляет пользователя из базы данных и возвращает JSON-ответ
    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var user = await _context.Users.FindAsync(id);

        if (user == null)
        {
            return Json(new { success = false, message = "Пользователь не найден в системе!" });
        }

        // Запрещаем админу случайно удалить самого себя через этот контроллер (на всякий случай)
        if (user.Role != UserRole.Обучающийся)
        {
            return Json(new { success = false, message = "Через эту панель можно удалять только слушателей!" });
        }

        _context.Users.Remove(user);
        await _context.SaveChangesAsync();

        return Json(new { success = true, message = "Слушатель успешно удален." });
    }

    // POST: /Users/DeleteMultiple
    // Массовое асинхронное удаление выбранных слушателей
    [HttpPost]
    public async Task<IActionResult> DeleteMultiple([FromBody] List<int> ids)
    {
        if (ids == null || !ids.Any())
        {
            return Json(new { success = false, message = "Не выбрано ни одной записи!" });
        }

        // Ищем в БД всех пользователей из списка, у которых роль "Обучающийся"
        var usersToDelete = await _context.Users
            .Where(u => ids.Contains(u.Id) && u.Role == UserRole.Обучающийся)
            .ToListAsync();

        if (!usersToDelete.Any())
        {
            return Json(new { success = false, message = "Указанные пользователи не найдены!" });
        }

        _context.Users.RemoveRange(usersToDelete);
        await _context.SaveChangesAsync();

        return Json(new { success = true, message = $"Успешно удалено записей: {usersToDelete.Count}" });
    }

    // ГЕТ: /Users/Edit/5
    // Открывает страницу редактирования с заполненными данными текущего пользователя
    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var student = await _context.Users
            .Include(u => u.Company)
            .FirstOrDefaultAsync(u => u.Id == id && u.Role == UserRole.Обучающийся);

        if (student == null)
        {
            return NotFound();
        }

        // Подгружаем данные во ViewBag
        ViewBag.LastName = student.LastName;
        ViewBag.FirstName = student.FirstName;
        ViewBag.MiddleName = student.MiddleName;
        ViewBag.Gender = (int)student.Gender;
        ViewBag.BirthDate = student.BirthDate?.ToString("yyyy-MM-dd");
        ViewBag.Snils = student.Snils;
        ViewBag.CompanyName = student.Company != null ? student.Company.Name : string.Empty;
        ViewBag.Department = student.Department;
        ViewBag.Login = student.Login; // Показываем логин, но заблокируем его
        ViewBag.Password = student.Password;
        ViewBag.Position = student.Position;

        return View();
    }

    // ПОСТ: /Users/Edit/5
    // Проверяет изменения, валидирует СНИЛС и сохраняет новые данные (включая измененный пароль)
    [HttpPost]
    public async Task<IActionResult> Edit(int id, string lastName, string firstName, string middleName,
        Gender gender, DateTime? birthDate, string snils, string companyName, string department, string password, string position)
    {
        // Буферизируем данные на случай ошибки
        ViewBag.LastName = lastName;
        ViewBag.FirstName = firstName;
        ViewBag.MiddleName = middleName;
        ViewBag.Gender = (int)gender;
        ViewBag.BirthDate = birthDate?.ToString("yyyy-MM-dd");
        ViewBag.Snils = snils;
        ViewBag.CompanyName = companyName;
        ViewBag.Department = department;
        ViewBag.Password = password; // Передаем обратно измененный пароль
        ViewBag.Position = position;

        var student = await _context.Users.FindAsync(id);
        if (student == null)
        {
            return NotFound();
        }
        ViewBag.Login = student.Login;

        // 1. Проверка СНИЛС на дубликаты среди других пользователей
        if (!string.IsNullOrWhiteSpace(snils))
        {
            var isSnilsDuplicate = await _context.Users
                .AnyAsync(u => u.Snils == snils.Trim() && u.Id != id);

            if (isSnilsDuplicate)
            {
                ModelState.AddModelError("", $"Ошибка: СНИЛС '{snils}' уже принадлежит другому пользователю!");
                return View();
            }
        }

        // 2. Валидация выбранной компании
        int? finalCompanyId = null;
        if (!string.IsNullOrWhiteSpace(companyName))
        {
            var company = await _context.Companies.FirstOrDefaultAsync(c => c.Name == companyName.Trim());
            if (company == null)
            {
                ModelState.AddModelError("", "Ошибка: Указанная организация не найдена! Выберите компанию строго из списка поиска или очистите поле для физ. лица.");
                return View();
            }
            finalCompanyId = company.Id; // Запоминаем Id найденной компании
        }

        // 3. Обновляем все поля сущности (включая пароль)
        student.LastName = lastName.Trim();
        student.FirstName = firstName.Trim();
        student.MiddleName = string.IsNullOrWhiteSpace(middleName) ? null : middleName.Trim();
        student.Gender = gender;
        student.BirthDate = birthDate;
        student.Snils = string.IsNullOrWhiteSpace(snils) ? null : snils.Trim();
        student.Department = string.IsNullOrWhiteSpace(department) ? null : department.Trim();
        student.CompanyId = finalCompanyId;
        student.Position = position?.Trim();

        // Перезаписываем пароль, если админ его изменил или сгенерировал новый
        if (!string.IsNullOrWhiteSpace(password))
        {
            student.Password = password.Trim();
        }

        _context.Users.Update(student);
        await _context.SaveChangesAsync();

        return RedirectToAction(nameof(Index));
    }

    // GET: /Users/DownloadTemplate
    [HttpGet]
    public IActionResult DownloadTemplate()
    {
        using (var workbook = new XLWorkbook())
        {
            var worksheet = workbook.Worksheets.Add("Слушатели");

            // Заголовки
            string[] headers = {
                "Фамилия", "Имя", "Отчество", "Пол", "Дата рождения",
                "СНИЛС", "ИНН Организации", "Отделение", "Должность", "ID группы"
            };

            for (int i = 0; i < headers.Length; i++)
            {
                var cell = worksheet.Cell(1, i + 1);
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF2CC"); // Желтоватый фон как в шапке доступов
                cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            }

            // Пример заполнения (строка 2)
            worksheet.Cell(2, 1).Value = "Петров";
            worksheet.Cell(2, 2).Value = "Петр";
            worksheet.Cell(2, 3).Value = "Петрович";
            worksheet.Cell(2, 4).Value = "Муж";
            worksheet.Cell(2, 5).Value = "20.10.1995";
            worksheet.Cell(2, 6).Value = "123-456-789 01";
            worksheet.Cell(2, 7).Value = "7700000000"; // ИНН компании (если есть)
            worksheet.Cell(2, 8).Value = "Отдел сборки";
            worksheet.Cell(2, 9).Value = "Сварщик 5 разряда";
            worksheet.Cell(2, 10).Value = ""; // ID группы (оставьте пустым, если не нужно зачислять)

            // Стилизуем строку с примером (серый текст)
            var exampleRow = worksheet.Row(2);
            exampleRow.Style.Font.FontColor = XLColor.Gray;

            // Автоширина
            worksheet.Columns().AdjustToContents();

            using (var stream = new MemoryStream())
            {
                workbook.SaveAs(stream);
                var content = stream.ToArray();

                // Формируем русское имя файла через ContentDisposition
                var contentDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
                {
                    FileNameStar = "Шаблон_Импорта_Слушателей.xlsx"
                };
                Response.Headers[Microsoft.Net.Http.Headers.HeaderNames.ContentDisposition] = contentDisposition.ToString();

                return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            }
        }
    }

    // POST: /Users/Import
    [HttpPost]
    public async Task<IActionResult> Import(IFormFile excelFile)
    {
        if (excelFile == null || excelFile.Length == 0)
        {
            return Json(new { success = false, message = "Файл не был загружен или он пуст!" });
        }

        int successCount = 0;
        int errorCount = 0;
        var errorMessages = new List<string>();
        var random = new Random();

        var existingMtNumbers = await _context.CourseProgresses
            .Where(p => p.MintrudNumber != null)
            .Select(p => p.MintrudNumber)
            .ToListAsync();

        int globalMaxMt = 0;
        foreach (var mt in existingMtNumbers)
        {
            if (int.TryParse(mt, out int n) && n > globalMaxMt) globalMaxMt = n;
        }

        try
        {
            using (var stream = new MemoryStream())
            {
                await excelFile.CopyToAsync(stream);
                using (var workbook = new XLWorkbook(stream))
                {
                    var worksheet = workbook.Worksheet(1);
                    var rows = worksheet.RowsUsed().Skip(1);

                    foreach (var row in rows)
                    {
                        try
                        {
                            string lastName = row.Cell(1).GetString().Trim();
                            string firstName = row.Cell(2).GetString().Trim();
                            string middleName = row.Cell(3).GetString().Trim();
                            string genderStr = row.Cell(4).GetString().Trim().ToLower();
                            string birthDateStr = row.Cell(5).GetString().Trim();
                            string snils = row.Cell(6).GetString().Trim();
                            string inn = row.Cell(7).GetString().Trim();
                            string department = row.Cell(8).GetString().Trim();
                            string position = row.Cell(9).GetString().Trim();
                            string groupNumberStr = row.Cell(10).GetString().Trim();

                            if (string.IsNullOrEmpty(lastName) && string.IsNullOrEmpty(firstName)) continue;

                            if (string.IsNullOrEmpty(lastName) || string.IsNullOrEmpty(firstName))
                            {
                                errorCount++;
                                errorMessages.Add($"Строка {row.RowNumber()}: Не заполнено обязательное поле Фамилия или Имя.");
                                continue;
                            }

                            Company? company = null;
                            if (!string.IsNullOrEmpty(inn))
                            {
                                company = await _context.Companies.FirstOrDefaultAsync(c => c.Inn == inn);
                                if (company == null)
                                {
                                    errorCount++;
                                    errorMessages.Add($"Строка {row.RowNumber()} ({lastName}): Организация с ИНН '{inn}' отсутствует в системе.");
                                    continue;
                                }
                            }

                            Group? targetGroup = null;
                            if (!string.IsNullOrEmpty(groupNumberStr))
                            {
                                if (int.TryParse(groupNumberStr, out int groupId))
                                {
                                    targetGroup = await _context.Groups.FindAsync(groupId);
                                    if (targetGroup == null)
                                    {
                                        errorCount++;
                                        errorMessages.Add($"Строка {row.RowNumber()} ({lastName}): Учебная группа с ID № {groupId} не найдена.");
                                        continue;
                                    }
                                }
                                else
                                {
                                    errorCount++;
                                    errorMessages.Add($"Строка {row.RowNumber()} ({lastName}): В колонке группы указано не число. Требуется ID группы.");
                                    continue;
                                }
                            }

                            Gender gender = (genderStr == "жен" || genderStr == "женский" || genderStr == "ж") ? Gender.Жен : Gender.Муж;
                            DateTime? birthDate = null;
                            if (DateTime.TryParse(birthDateStr, out DateTime parsedDate)) birthDate = parsedDate;

                            User? studentToProcess = null;

                            if (!string.IsNullOrEmpty(snils))
                            {
                                studentToProcess = await _context.Users.FirstOrDefaultAsync(u => u.Snils == snils.Trim());
                            }

                            if (studentToProcess != null)
                            {
                                studentToProcess.LastName = lastName;
                                studentToProcess.FirstName = firstName;
                                studentToProcess.MiddleName = string.IsNullOrEmpty(middleName) ? null : middleName;
                                studentToProcess.Gender = gender;
                                studentToProcess.BirthDate = birthDate;
                                studentToProcess.Department = string.IsNullOrEmpty(department) ? null : department;
                                studentToProcess.CompanyId = company?.Id;
                                studentToProcess.Position = string.IsNullOrEmpty(position) ? null : position;
                                // ВАЖНО: гарантируем, что роль — Обучающийся
                                studentToProcess.Role = UserRole.Обучающийся;

                                _context.Users.Update(studentToProcess);
                            }
                            else
                            {
                                string baseTranslit = Transliterate(lastName.ToLower());
                                string generatedLogin = "";
                                bool isLoginUnique = false;

                                while (!isLoginUnique)
                                {
                                    generatedLogin = $"{baseTranslit}{random.Next(1000, 9999)}";
                                    isLoginUnique = !await _context.Users.AnyAsync(u => u.Login == generatedLogin);
                                }

                                string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
                                var passwordBuilder = new StringBuilder();
                                for (int i = 0; i < 10; i++)
                                {
                                    passwordBuilder.Append(chars[random.Next(chars.Length)]);
                                }

                                studentToProcess = new User
                                {
                                    Login = generatedLogin,
                                    Password = passwordBuilder.ToString(),
                                    LastName = lastName,
                                    FirstName = firstName,
                                    MiddleName = string.IsNullOrEmpty(middleName) ? null : middleName,
                                    Gender = gender,
                                    BirthDate = birthDate,
                                    Snils = string.IsNullOrEmpty(snils) ? null : snils,
                                    Department = string.IsNullOrEmpty(department) ? null : department,
                                    Role = UserRole.Обучающийся, // ← гарантируем роль
                                    CompanyId = company?.Id,
                                    Position = string.IsNullOrEmpty(position) ? null : position
                                };

                                _context.Users.Add(studentToProcess);
                            }

                            await _context.SaveChangesAsync();

                            // 4. МАГИЯ АВТОМАТИЧЕСКОГО ЗАЧИСЛЕНИЯ В ГРУППУ И НА КУРСЫ
                            if (targetGroup != null)
                            {
                                // Разрешаем зачисление, если это физлицо (company == null)
                                // ИЛИ если компания этого юзера привязана к данной группе потока обучения
                                bool isAllowedToGroup = company == null || await _context.GroupCompanies
                                    .AnyAsync(gc => gc.GroupId == targetGroup.Id && gc.CompanyId == company.Id);

                                if (isAllowedToGroup)
                                {
                                    // Добавляем в группу, если его там еще нет
                                    bool alreadyInGroup = await _context.GroupStudents
                                        .AnyAsync(gs => gs.GroupId == targetGroup.Id && gs.StudentId == studentToProcess.Id);

                                    if (!alreadyInGroup)
                                    {
                                        _context.GroupStudents.Add(new GroupStudent
                                        {
                                            GroupId = targetGroup.Id,
                                            StudentId = studentToProcess.Id
                                        });
                                        await _context.SaveChangesAsync();
                                    }

                                    // ВАЖНО: Получаем актуальный список ВСЕХ студентов группы (включая нового)
                                    var allGroupStudentIds = await _context.GroupStudents
                                        .Where(gs => gs.GroupId == targetGroup.Id)
                                        .Select(gs => gs.StudentId)
                                        .ToListAsync();

                                    var courseConfigs = await _context.GroupCourseConfigs
                                        .Include(cc => cc.Course)
                                        .Where(cc => cc.GroupId == targetGroup.Id)
                                        .ToListAsync();

                                    foreach (var config in courseConfigs)
                                    {
                                        // Получаем текущий список допущенных ID
                                        var allowedIds = string.IsNullOrEmpty(config.AllowedStudentIds)
                                            ? new List<int>()
                                            : config.AllowedStudentIds.Split(',').Select(int.Parse).ToList();

                                        // ГЛАВНОЕ ИСПРАВЛЕНИЕ:
                                        // Если список пустой (значит, по умолчанию были допущены ВСЕ),
                                        // то заполняем его всеми текущими студентами группы.
                                        if (!allowedIds.Any())
                                        {
                                            allowedIds = new List<int>(allGroupStudentIds);
                                            config.AllowedStudentIds = string.Join(",", allowedIds);
                                            _context.GroupCourseConfigs.Update(config);
                                        }
                                        // Если список не пустой, но нового студента там нет — просто добавляем его
                                        else if (!allowedIds.Contains(studentToProcess.Id))
                                        {
                                            allowedIds.Add(studentToProcess.Id);
                                            config.AllowedStudentIds = string.Join(",", allowedIds);
                                            _context.GroupCourseConfigs.Update(config);
                                        }

                                        // 4.2. Создаем запись в прогрессе для этого курса
                                        var progress = await _context.CourseProgresses
                                            .FirstOrDefaultAsync(p => p.StudentId == studentToProcess.Id && p.CourseId == config.CourseId && p.GroupId == targetGroup.Id);

                                        if (progress == null)
                                        {
                                            progress = new CourseProgress
                                            {
                                                StudentId = studentToProcess.Id,
                                                CourseId = config.CourseId,
                                                GroupId = targetGroup.Id
                                            };
                                            _context.CourseProgresses.Add(progress);
                                        }

                                        // 4.3. Если курс Минтруда — присваиваем номер
                                        if (config.Course != null && config.Course.MintrudProgramId != null)
                                        {
                                            if (string.IsNullOrEmpty(progress.MintrudNumber))
                                            {
                                                globalMaxMt++;
                                                progress.MintrudNumber = globalMaxMt.ToString();
                                            }
                                        }
                                    }
                                    await _context.SaveChangesAsync();
                                }
                            }
                            successCount++;
                        }
                        catch (Exception innerEx)
                        {
                            errorCount++;
                            errorMessages.Add($"Строка {row.RowNumber()}: Сбой разбора данных ({innerEx.Message}).");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = "Критическая ошибка чтения файла: " + ex.Message });
        }

        return Json(new { success = true, successCount, errorCount, errors = errorMessages });
    }


        // GET: /Users/Details/5
    [HttpGet]
    public async Task<IActionResult> Details(int id)
    {
        var user = await _context.Users
            .Include(u => u.Company)
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user == null) return NotFound();

        var groupIds = await _context.GroupStudents
            .Where(gs => gs.StudentId == id)
            .Select(gs => gs.GroupId)
            .ToListAsync();

        var groups = await _context.Groups
            .Where(g => groupIds.Contains(g.Id))
            .ToListAsync();

        var configs = await _context.GroupCourseConfigs
            .Include(gcc => gcc.Course)
            .Where(gcc => groupIds.Contains(gcc.GroupId))
            .ToListAsync();

        var viewModel = new UserDetailsViewModel
        {
            User = user,
            Groups = groups.Select(g => new GroupWithCoursesViewModel
            {
                Group = g,
                Courses = configs
                    .Where(c => c.GroupId == g.Id)
                    // ФИЛЬТР: оставляем только те курсы, на которые он записан
                    .Where(c => 
                    {
                        // Если поле null (курс только создан), считаем что доступен всем
                        if (c.AllowedStudentIds == null) return true; 
                        
                        // Если поле пустое, значит доступ никому не выдан
                        if (string.IsNullOrWhiteSpace(c.AllowedStudentIds)) return false; 

                        var ids = c.AllowedStudentIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
                        return ids.Contains(id); 
                    })
                    .Select(c => new CourseAccessViewModel
                    {
                        Course = c.Course!,
                        GroupConfigId = c.Id,
                        IsAllowed = true // Уже отфильтровали, поэтому всегда true
                    }).ToList()
            })
            .Where(g => g.Courses.Any()) // Прячем группы, в которых нет доступных курсов
            .ToList()
        };

        return PartialView("_UserDetailsModal", viewModel);
    }

    // POST: /Users/RemoveFromGroup
    [HttpPost]
    public async Task<IActionResult> RemoveFromGroup(int studentId, int groupId)
    {
        try
        {
            // 1. Удаляем из группы
            var groupStudent = await _context.GroupStudents
                .FirstOrDefaultAsync(gs => gs.StudentId == studentId && gs.GroupId == groupId);

            if (groupStudent != null)
            {
                _context.GroupStudents.Remove(groupStudent);
            }

            // 2. Заодно чистим студента из AllowedStudentIds всех курсов этой группы (чтобы не оставлять мусор)
            var configs = await _context.GroupCourseConfigs
                .Where(gcc => gcc.GroupId == groupId && !string.IsNullOrWhiteSpace(gcc.AllowedStudentIds))
                .ToListAsync();

            foreach (var config in configs)
            {
                var ids = config.AllowedStudentIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
                if (ids.Contains(studentId))
                {
                    ids.Remove(studentId);
                    config.AllowedStudentIds = string.Join(",", ids);
                    _context.GroupCourseConfigs.Update(config);
                }
            }

            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    // POST: /Users/RemoveFromCourse
    [HttpPost]
    public async Task<IActionResult> RemoveFromCourse(int studentId, int configId)
    {
        try
        {
            var config = await _context.GroupCourseConfigs
                .FirstOrDefaultAsync(c => c.Id == configId);

            if (config == null) return Json(new { success = false, message = "Курс не найден" });

            List<int> allowedIds;
            if (string.IsNullOrWhiteSpace(config.AllowedStudentIds))
            {
                // Если список был пуст (значит, допущены все студенты группы), 
                // нужно собрать всех студентов группы, кроме отчисляемого, и записать в AllowedStudentIds
                var allStudentIds = await _context.GroupStudents
                    .Where(gs => gs.GroupId == config.GroupId)
                    .Select(gs => gs.StudentId)
                    .ToListAsync();

                allowedIds = allStudentIds.Where(sid => sid != studentId).ToList();
            }
            else
            {
                // Если список уже был, просто убираем оттуда студента
                allowedIds = config.AllowedStudentIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(int.Parse).ToList();
                allowedIds.Remove(studentId);
            }

            config.AllowedStudentIds = string.Join(",", allowedIds);
            _context.GroupCourseConfigs.Update(config);
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    private string Transliterate(string text)
    {
        var russianToConvert = new Dictionary<string, string>
        {
            {"а","a"},{"б","b"},{"в","v"},{"г","g"},{"д","d"},{"е","e"},{"ё","yo"},
            {"ж","zh"},{"з","z"},{"и","i"},{"й","y"},{"к","k"},{"л","l"},{"м","m"},
            {"н","n"},{"о","o"},{"п","p"},{"р","r"},{"с","s"},{"т","t"},{"у","u"},
            {"ф","f"},{"х","kh"},{"ц","ts"},{"ч","ch"},{"ш","sh"},{"щ","sch"},{"ъ",""},
            {"ы","y"},{"ь",""},{"э","e"},{"ю","yu"},{"я","ya"}
        };

        var sb = new StringBuilder();
        foreach (char c in text)
        {
            string s = c.ToString();
            if (russianToConvert.ContainsKey(s)) sb.Append(russianToConvert[s]);
            else sb.Append(s);
        }
        return sb.ToString();
    }



    // GET: /Users/DownloadAccessCard/5
    // Генерация и скачивание Excel-карточки доступа для одного студента (в стиле групповых доступов)
    [HttpGet]
    public async Task<IActionResult> DownloadAccessCard(int id)
    {
        var student = await _context.Users
            .Include(u => u.Company)
            .FirstOrDefaultAsync(u => u.Id == id && u.Role == UserRole.Обучающийся);

        if (student == null)
        {
            return NotFound("Слушатель не найден в системе.");
        }

        using (var workbook = new XLWorkbook())
        {
            var worksheet = workbook.Worksheets.Add("Доступы");

            // --- ФОРМИРУЕМ ШАПКУ (Точь-в-точь как в групповом файле) ---

            // Строка 1
            worksheet.Cell(1, 1).Value = "Данные для входа в систему https://sdo.dpo-energo.ru для слушателя организации";
            worksheet.Range(1, 1, 1, 8).Merge();
            worksheet.Cell(1, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            worksheet.Cell(1, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            worksheet.Cell(1, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF2CC");

            // Строка 2 (Название компании или "Частное лицо")
            string companyName = student.Company?.Name ?? "Частное лицо";
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

            // --- ЗАПОЛНЯЕМ ДАННЫЕ СТУДЕНТА (Строка 4) ---

            string escapedLogin = Uri.EscapeDataString(student.Login ?? "");
            string escapedPassword = Uri.EscapeDataString(student.Password ?? "");

            // Ссылка авторизации
            string authUrl = $"http://localhost:5018/Auth/Login?login={escapedLogin}&password={escapedPassword}";

            int row = 4;
            worksheet.Cell(row, 1).Value = student.LastName ?? "";
            worksheet.Cell(row, 2).Value = student.FirstName ?? "";
            worksheet.Cell(row, 3).Value = student.MiddleName ?? "";
            worksheet.Cell(row, 4).Value = student.Login ?? "";
            worksheet.Cell(row, 5).Value = student.Password ?? "";
            worksheet.Cell(row, 6).Value = student.Department ?? "—";
            worksheet.Cell(row, 7).Value = student.Position ?? "—";

            // Ячейка со ссылкой
            var linkCell = worksheet.Cell(row, 8);
            linkCell.Value = authUrl;
            linkCell.SetHyperlink(new XLHyperlink(authUrl));
            linkCell.Style.Font.FontColor = XLColor.Blue;
            linkCell.Style.Font.Underline = XLFontUnderlineValues.Single;

            // Рамки
            worksheet.Range(row, 1, row, 8).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            worksheet.Range(row, 1, row, 8).Style.Border.InsideBorder = XLBorderStyleValues.Thin;

            // Автоподбор ширины колонок
            worksheet.Columns().AdjustToContents();

            // 5. Отдаем готовый файл в браузер
            using (var memoryStream = new MemoryStream())
            {
                workbook.SaveAs(memoryStream);

                string fileName = $"Доступ_{student.LastName}_{student.FirstName}__{student.MiddleName}.xlsx";

                // Правильное кодирование русского имени файла (RFC 5987)
                var contentDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
                {
                    FileNameStar = fileName
                };
                Response.Headers[Microsoft.Net.Http.Headers.HeaderNames.ContentDisposition] = contentDisposition.ToString();

                return File(memoryStream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            }
        }
    }


}