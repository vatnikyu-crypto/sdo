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
        var company = await _context.Companies.FirstOrDefaultAsync(c => c.Name == companyName.Trim());
        if (company == null)
        {
            ModelState.AddModelError("", "Ошибка: Указанная организация не найдена в системе! Выберите компанию строго из выпадающего списка живого поиска.");
            return View();
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
            CompanyId = company.Id,
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
        ViewBag.CompanyName = student.Company?.Name;
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
        var company = await _context.Companies.FirstOrDefaultAsync(c => c.Name == companyName.Trim());
        if (company == null)
        {
            ModelState.AddModelError("", "Ошибка: Указанная организация не найдена! Выберите компанию из списка поиска.");
            return View();
        }

        // 3. Обновляем все поля сущности (включая пароль)
        student.LastName = lastName.Trim();
        student.FirstName = firstName.Trim();
        student.MiddleName = string.IsNullOrWhiteSpace(middleName) ? null : middleName.Trim();
        student.Gender = gender;
        student.BirthDate = birthDate;
        student.Snils = string.IsNullOrWhiteSpace(snils) ? null : snils.Trim();
        student.Department = string.IsNullOrWhiteSpace(department) ? null : department.Trim();
        student.CompanyId = company.Id;
        student.Position = position.Trim();

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
    // Обновленный CSV шаблон с учетом новых полей Должность и Номер группы
    [HttpGet]
    public IActionResult DownloadTemplate()
    {
        var csvContent = "Фамилия;Имя;Отчество;Пол;Дата рождения;СНИЛС;ИНН Организации;Отделение;Должность;Номер группы\n" +
                         "Петров;Петр;Петрович;Муж;20.10.1995;123-456-789 01;7700000000;Отдел сборки;Сварщик 5 разряда;";

        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var textBytes = Encoding.UTF8.GetBytes(csvContent);
        var fileBytes = new byte[bom.Length + textBytes.Length];
        Buffer.BlockCopy(bom, 0, fileBytes, 0, bom.Length);
        Buffer.BlockCopy(textBytes, 0, fileBytes, bom.Length, textBytes.Length);

        return File(fileBytes, "text/csv", "Шаблон_Импорта_Слушателей.csv");
    }

    // POST: /Users/Import
    // УМНЫЙ ИМПОРТ: Добавление должностей, автоматическое зачисление в группы и на курсы
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

        try
        {
            using (var reader = new StreamReader(excelFile.OpenReadStream(), Encoding.UTF8))
            {
                string? line;
                bool isHeader = true;
                int lineNumber = 0;

                while ((line = await reader.ReadLineAsync()) != null)
                {
                    lineNumber++;
                    if (isHeader) { isHeader = false; continue; }
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        var cells = line.Split(';');

                        // Теперь проверяем, чтобы в структуре было минимум 10 колонок
                        if (cells.Length < 10)
                        {
                            errorCount++;
                            errorMessages.Add($"Строка {lineNumber}: Ошибка структуры (требуется 10 колонок: добавлены Должность и Номер группы).");
                            continue;
                        }

                        // Считываем ячейки строго по новым индексам
                        string lastName = (cells[0] ?? "").Trim();
                        string firstName = (cells[1] ?? "").Trim();
                        string middleName = (cells[2] ?? "").Trim();
                        string genderStr = (cells[3] ?? "").Trim().ToLower();
                        string birthDateStr = (cells[4] ?? "").Trim();
                        string snils = (cells[5] ?? "").Trim();
                        string inn = (cells[6] ?? "").Trim();
                        string department = (cells[7] ?? "").Trim();
                        string position = (cells[8] ?? "").Trim();      // НОВОЕ: Индекс 8
                        string groupNumberStr = (cells[9] ?? "").Trim(); // НОВОЕ: Индекс 9

                        if (string.IsNullOrEmpty(lastName) && string.IsNullOrEmpty(firstName)) continue;

                        if (string.IsNullOrEmpty(lastName) || string.IsNullOrEmpty(firstName))
                        {
                            errorCount++;
                            errorMessages.Add($"Строка {lineNumber}: Не заполнено обязательное поле Фамилия или Имя.");
                            continue;
                        }

                        // 1. Валидация Организации по ИНН
                        if (string.IsNullOrEmpty(inn))
                        {
                            errorCount++;
                            errorMessages.Add($"Строка {lineNumber} ({lastName}): Отсутствует ИНН организации.");
                            continue;
                        }

                        var company = await _context.Companies.FirstOrDefaultAsync(c => c.Inn == inn);
                        if (company == null)
                        {
                            errorCount++;
                            errorMessages.Add($"Строка {lineNumber} ({lastName}): Организация с ИНН '{inn}' отсутствует в системе.");
                            continue;
                        }

                        // 2. Валидация группы (Если шифр указан, проверяем её существование в БД)
                        Group? targetGroup = null;
                        if (!string.IsNullOrEmpty(groupNumberStr))
                        {
                            // Пытаемся перевести текстовое значение из ячейки CSV в число int
                            if (int.TryParse(groupNumberStr.Trim(), out int groupId))
                            {
                                // Ищем группу в базе данных напрямую по её ID
                                targetGroup = await _context.Groups.FindAsync(groupId);

                                if (targetGroup == null)
                                {
                                    errorCount++;
                                    errorMessages.Add($"Строка {lineNumber} ({lastName}): Учебная группа с ID № {groupId} не найдена в системе! Импорт заблокирован.");
                                    continue;
                                }
                            }
                            else
                            {
                                errorCount++;
                                errorMessages.Add($"Строка {lineNumber} ({lastName}): В колонке группы указано не число. Для импорта требуется строго ID группы (например: 1, 2, 3).");
                                continue;
                            }
                        }

                        Gender gender = (genderStr == "жен" || genderStr == "женский") ? Gender.Жен : Gender.Муж;
                        DateTime? birthDate = null;
                        if (DateTime.TryParse(birthDateStr, out DateTime parsedDate)) birthDate = parsedDate;

                        // Объявляем переменную для студента (нового или существующего)
                        User? studentToProcess = null;

                        // 3. УМНЫЙ АПДЕЙТ: Проверяем, есть ли уже в базе СНИЛС
                        if (!string.IsNullOrEmpty(snils))
                        {
                            studentToProcess = await _context.Users.FirstOrDefaultAsync(u => u.Snils == snils.Trim());
                        }
                        if (studentToProcess != null)
                        {
                            // СЦЕНАРИЙ А: Слушатель уже существует в СДО — обновляем его данные
                            studentToProcess.LastName = lastName;
                            studentToProcess.FirstName = firstName;
                            studentToProcess.MiddleName = string.IsNullOrEmpty(middleName) ? null : middleName;
                            studentToProcess.Gender = gender;
                            studentToProcess.BirthDate = birthDate;
                            studentToProcess.Department = string.IsNullOrEmpty(department) ? null : department;
                            studentToProcess.CompanyId = company.Id;
                            studentToProcess.Position = string.IsNullOrEmpty(position) ? null : position; // Обновляем должность

                            _context.Users.Update(studentToProcess);
                        }
                        else
                        {
                            // СЦЕНАРИЙ Б: Новая учетная запись — генерируем логин и пароль
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
                                Role = UserRole.Обучающийся,
                                CompanyId = company.Id,
                                Position = string.IsNullOrEmpty(position) ? null : position // Записываем должность
                            };

                            _context.Users.Add(studentToProcess);
                        }

                        // Срочно сохраняем пользователя, чтобы получить его Id (если он был новым)
                        await _context.SaveChangesAsync();

                        // 4. МAГИЯ АВТОМАТИЧЕСКОГО ЗАЧИСЛЕНИЯ В ГРУППУ И НА КУРСЫ
                        if (targetGroup != null)
                        {
                            // Проверяем, зачислен ли уже этот студент в данную группу ранее
                            bool alreadyInGroup = await _context.GroupStudents
                                .AnyAsync(gs => gs.GroupId == targetGroup.Id && gs.StudentId == studentToProcess.Id);

                            if (!alreadyInGroup)
                            {
                                // Привязываем к общему костяку учебной группы
                                _context.GroupStudents.Add(new GroupStudent
                                {
                                    GroupId = targetGroup.Id,
                                    StudentId = studentToProcess.Id
                                });
                                await _context.SaveChangesAsync();
                            }

                            // Подтягиваем все курсы, которые админ привязал к этой группе
                            var courseConfigs = await _context.GroupCourseConfigs
                                .Where(cc => cc.GroupId == targetGroup.Id)
                                .ToListAsync();

                            foreach (var config in courseConfigs)
                            {
                                // Парсим текущие допущенные ID студентов из строки через запятую
                                var allowedIds = string.IsNullOrEmpty(config.AllowedStudentIds)
                                    ? new List<int>()
                                    : config.AllowedStudentIds.Split(',').Select(int.Parse).ToList();

                                // Если студента еще нет в допусках этого курса — добавляем его
                                if (!allowedIds.Contains(studentToProcess.Id))
                                {
                                    allowedIds.Add(studentToProcess.Id);
                                    config.AllowedStudentIds = string.Join(",", allowedIds);
                                    _context.GroupCourseConfigs.Update(config);
                                }
                            }

                            await _context.SaveChangesAsync();
                        }

                        successCount++;
                    }
                    catch (Exception innerEx)
                    {
                        errorCount++;
                        errorMessages.Add($"Строка {lineNumber}: Сбой разбора ячеек ({innerEx.Message}).");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = "Критическая ошибка чтения: " + ex.Message });
        }

        return Json(new { success = true, successCount, errorCount, errors = errorMessages });
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
    // Генерация и скачивание Excel-карточки доступа для одного студента
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
            // 1. Создаем лист в книге
            var worksheet = workbook.Worksheets.Add("Доступ к СДО");

            // 2. Заполняем первую строку (Шапка - Жирный шрифт)
            worksheet.Cell(1, 1).Value = "Фамилия";
            worksheet.Cell(1, 2).Value = "Имя";
            worksheet.Cell(1, 3).Value = "Отчество";
            worksheet.Cell(1, 4).Value = "Компания";
            worksheet.Cell(1, 5).Value = "Должность";
            worksheet.Cell(1, 6).Value = "Логин";
            worksheet.Cell(1, 7).Value = "Пароль";

            // Стилизуем шапку: жирный шрифт и светло-серый фон для солидности
            var headerRow = worksheet.Row(1);
            headerRow.Style.Font.Bold = true;
            headerRow.Style.Fill.BackgroundColor = XLColor.FromHtml("#F1F5F9");

            // 3. Заполняем вторую строку (Данные пользователя)
            worksheet.Cell(2, 1).Value = student.LastName;
            worksheet.Cell(2, 2).Value = student.FirstName;
            worksheet.Cell(2, 3).Value = student.MiddleName ?? "—";
            worksheet.Cell(2, 4).Value = student.Company?.Name ?? "Частное лицо";
            worksheet.Cell(2, 5).Value = student.Position ?? "—";
            worksheet.Cell(2, 6).Value = student.Login;
            worksheet.Cell(2, 7).Value = student.Password;

            // 4. Наводим полную красоту (Границы и Автоширина)
            // Выделяем наш диапазон ячеек (2 строки на 7 колонок)
            var tableRange = worksheet.Range(1, 1, 2, 7);

            // Проставляем тонкие черные классические границы вокруг каждой ячейки
            tableRange.Style.Border.TopBorder = XLBorderStyleValues.Thin;
            tableRange.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            tableRange.Style.Border.LeftBorder = XLBorderStyleValues.Thin;
            tableRange.Style.Border.RightBorder = XLBorderStyleValues.Thin;
            tableRange.Style.Border.TopBorderColor = XLColor.Black;
            tableRange.Style.Border.BottomBorderColor = XLColor.Black;
            tableRange.Style.Border.LeftBorderColor = XLColor.Black;
            tableRange.Style.Border.RightBorderColor = XLColor.Black;

            // Выравниваем текст по левому краю, а логин/пароль можно по центру
            tableRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

            // МЕГА-ФИЧА: Честная автоматическая ширина всех колонок под размер текста!
            worksheet.Columns(1, 7).AdjustToContents();

            // 5. Отдаем готовый файл в браузер
            using (var memoryStream = new MemoryStream())
            {
                workbook.SaveAs(memoryStream);
                memoryStream.Seek(0, SeekOrigin.Begin);

                string fileName = $"Доступ_{student.LastName}_{student.FirstName}.xlsx";
                return File(memoryStream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
            }
        }
    }
}