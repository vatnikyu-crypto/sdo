using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models;
using SdoApp.Services;
using Xceed.Document.NET;
using Xceed.Words.NET;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SdoApp.Controllers;

[Authorize(Roles = "Обучающийся")]
public class StudentTestController : Controller
{
    private readonly AppDbContext _context;

    public StudentTestController(AppDbContext context)
    {
        _context = context;
    }

    // GET: /StudentTest/GetTestState?materialId=5
    // Используется фронтендом для вывода таблицы попыток и проверки доступности кнопок
    [HttpGet]
    public async Task<IActionResult> GetTestState(int materialId)
    {
        int studentId = GetCurrentUserId();
        if (studentId == 0) return Json(new { success = false, message = "Не авторизован" });

        var material = await _context.CourseMaterials.FindAsync(materialId);
        if (material == null || material.Type != MaterialType.Test)
            return Json(new { success = false, message = "Тест не найден" });

        // Вытаскиваем всю историю попыток студента по этому тесту
        var attempts = await _context.TestAttempts
            .Where(a => a.CourseMaterialId == materialId && a.StudentId == studentId)
            .OrderByDescending(a => a.StartedAt)
            .ToListAsync();

        // Ищем активную (InProgress) попытку
        var activeAttempt = attempts.FirstOrDefault(a => a.Status == AttemptStatus.InProgress);
        bool canContinue = false;
        int remainingSeconds = 0;

        if (activeAttempt != null)
        {
            if (activeAttempt.HardDeadline.HasValue)
            {
                // Для итогового проверяем сквозной таймер дедлайна
                if (DateTime.UtcNow < activeAttempt.HardDeadline.Value)
                {
                    canContinue = true;
                    remainingSeconds = (int)(activeAttempt.HardDeadline.Value - DateTime.UtcNow).TotalSeconds;
                }
                else
                {
                    // Время вышло, пока вкладка была закрыта — автоматически закрываем попытку
                    activeAttempt.Status = AttemptStatus.Failed;
                    activeAttempt.FinishedAt = activeAttempt.HardDeadline;
                    await _context.SaveChangesAsync();
                    activeAttempt = null;
                }
            }
            else
            {
                canContinue = true;
            }
        }

        // Форматируем историю для таблицы на фронтенде
        var historyData = attempts.Select(a => new
        {
            id = a.Id,
            status = a.Status == AttemptStatus.InProgress ? "В процессе" :
                     a.Status == AttemptStatus.Passed ? "Успешно" :
                     a.Status == AttemptStatus.Failed ? "Не сдан" : "Аннулирован",
            statusEnum = a.Status.ToString(),
            date = a.StartedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm"),
            score = $"{a.CorrectAnswersCount} из {a.TotalQuestionsCount} ({a.ScorePercentage}%)"
        }).ToList();

        return Json(new
        {
            success = true,
            canContinue = canContinue,
            activeAttemptId = activeAttempt?.Id ?? 0,
            remainingSeconds = remainingSeconds,
            history = historyData
        });
    }

    private int GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst("UserId")?.Value;
        return int.TryParse(userIdClaim, out int userId) ? userId : 0;
    }
    // GET: /StudentTest/Take?testId=5
    // Запуск или продолжение тестирования (Экран с вопросами)
    [HttpGet]
    public async Task<IActionResult> Take(int testId)
    {
        int studentId = GetCurrentUserId();
        if (studentId == 0) return Challenge();

        var testMaterial = await _context.CourseMaterials.FindAsync(testId);
        if (testMaterial == null || testMaterial.Type != MaterialType.Test)
            return NotFound("Тест не найден в системе.");

        // Проверяем, есть ли уже активная попытка
        var activeAttempt = await _context.TestAttempts
            .FirstOrDefaultAsync(a => a.CourseMaterialId == testId && a.StudentId == studentId && a.Status == AttemptStatus.InProgress);

        if (activeAttempt == null)
        {

            var oldInProgress = await _context.TestAttempts
                .Where(a => a.CourseMaterialId == testId && a.StudentId == studentId && a.Status == AttemptStatus.InProgress)
                .ToListAsync();
            foreach (var old in oldInProgress)
            {
                old.Status = AttemptStatus.Abandoned;
                old.FinishedAt = DateTime.UtcNow;
            }


            // Вычисляем жесткий дедлайн (если задан лимит времени в минутах)
            DateTime? deadline = null;
            if (testMaterial.TimeLimit > 0)
            {
                deadline = DateTime.UtcNow.AddMinutes(testMaterial.TimeLimit);
            }

            // Создаем запись новой попытки
            activeAttempt = new TestAttempt
            {
                CourseMaterialId = testId,
                StudentId = studentId,
                Status = AttemptStatus.InProgress,
                StartedAt = DateTime.UtcNow,
                HardDeadline = deadline
            };
            _context.TestAttempts.Add(activeAttempt);
            await _context.SaveChangesAsync();
        }
        else if (activeAttempt.HardDeadline.HasValue && DateTime.UtcNow > activeAttempt.HardDeadline.Value)
        {
            // Защита: если студент зашел по прямой старой ссылке на итоговый, а время вышло
            activeAttempt.Status = AttemptStatus.Failed;
            activeAttempt.FinishedAt = activeAttempt.HardDeadline;
            await _context.SaveChangesAsync();
            return RedirectToAction("Student", "Dashboard");
        }

        // Забираем вопросы теста
        var finalQuestions = await _context.TestQuestions
            .Include(q => q.Answers)
            .Where(q => q.CourseMaterialId == testId)
            .ToListAsync();

        // Перемешивание на основе сида попытки (чтобы порядок не ломался при обновлении страницы)
        if (testMaterial.ShuffleQuestions && finalQuestions.Any())
        {
            var random = new Random(activeAttempt.Id);
            finalQuestions = finalQuestions.OrderBy(q => random.Next()).ToList();
        }

        // Ограничение по количеству
        if (testMaterial.QuestionsCountToUse > 0 && finalQuestions.Count > testMaterial.QuestionsCountToUse)
        {
            finalQuestions = finalQuestions.Take(testMaterial.QuestionsCountToUse).ToList();
        }

        // Фиксируем общее число вопросов в этой сессии
        if (activeAttempt.TotalQuestionsCount == 0)
        {
            activeAttempt.TotalQuestionsCount = finalQuestions.Count;
            await _context.SaveChangesAsync();
        }

        ViewBag.AttemptId = activeAttempt.Id;
        ViewBag.TestId = testMaterial.Id;
        ViewBag.TestTitle = testMaterial.Title;
        ViewBag.TestKind = testMaterial.TestKind;

        // Передаем точное число секунд для запуска таймера на фронте
        ViewBag.RemainingSeconds = activeAttempt.HardDeadline.HasValue
            ? (int)Math.Max(0, (activeAttempt.HardDeadline.Value - DateTime.UtcNow).TotalSeconds)
            : 0;

        return View(finalQuestions);
    }

    // GET: /StudentTest/DownloadResult?attemptId=123
    [HttpGet]
    public async Task<IActionResult> DownloadResult(int attemptId)
    {
        int studentId = GetCurrentUserId();
        if (studentId == 0) return Challenge();

        var attempt = await _context.TestAttempts
            .Include(a => a.CourseMaterial).ThenInclude(m => m!.Course)
            .Include(a => a.Student)
            .ThenInclude(u => u!.Company)
            .FirstOrDefaultAsync(a => a.Id == attemptId && a.StudentId == studentId);

        if (attempt == null) return NotFound("Протокол тестирования не найден.");

        // 1. Десериализуем сохраненные ответы студента (QuestionId -> List<AnswerOptionId>)
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

        // Извлекаем только те ID вопросов, которые реально были в билете у студента
        var answeredQuestionIds = studentAnswers.Keys.ToList();

        // Вытаскиваем из базы строго эти вопросы
        var questions = await _context.TestQuestions
            .Include(q => q.Answers)
            .Where(q => answeredQuestionIds.Contains(q.Id))
            .ToListAsync();

        using (var memoryStream = new MemoryStream())
        {
            using (var doc = DocX.Create(memoryStream))
            {
                // ШАПКА ОРГАНИЗАЦИИ
                var pHeader = doc.InsertParagraph("АВТОНОМНАЯ НЕКОММЕРЧЕСКАЯ ОРГАНИЗАЦИЯ ДОПОЛНИТЕЛЬНОГО\nПРОФЕССИОНАЛЬНОГО ОБРАЗОВАНИЯ \"ЭНЕРГЕТИК\"");
                pHeader.Alignment = Alignment.center;
                pHeader.FontSize(10).Bold();
                doc.InsertParagraph("\n");

                // ЗАГОЛОВОК ДОКУМЕНТА
                var pTitle = doc.InsertParagraph($"ИТОГОВЫЙ ЭКЗАМЕН № {attempt.Id}-{attempt.StudentId}");
                pTitle.Alignment = Alignment.center;
                pTitle.FontSize(14).Bold();

                // ДАТА БЛАНКА
                var pDate = doc.InsertParagraph($"«{attempt.StartedAt.ToLocalTime().ToString("dd")}» {attempt.StartedAt.ToLocalTime().ToString("MMMM yyyy")} г.");
                pDate.Alignment = Alignment.right;
                pDate.FontSize(11).Italic();
                doc.InsertParagraph("\n");

                // ПЕРСОНАЛЬНЫЕ ДАННЫХ СЛУШАТЕЛЯ
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

                // ТАБЛИЦА РЕЗУЛЬТАТОВ (Строки = Количество реально выпавших вопросов + Шапка)
                int rowsCount = questions.Count + 1;
                var table = doc.AddTable(rowsCount, 4);
                table.Alignment = Alignment.center;
                table.Design = TableDesign.TableGrid;

                // Заполняем Шапку таблицы
                table.Rows[0].Cells[0].Paragraphs[0].Append("№").Bold().Alignment = Alignment.center;
                table.Rows[0].Cells[1].Paragraphs[0].Append("Вопрос").Bold().Alignment = Alignment.center;
                table.Rows[0].Cells[2].Paragraphs[0].Append("Ответ слушателя").Bold().Alignment = Alignment.center;
                table.Rows[0].Cells[3].Paragraphs[0].Append("Результат").Bold().Alignment = Alignment.center;

                int idx = 1;
                foreach (var q in questions)
                {
                    var row = table.Rows[idx];
                    row.Cells[0].Paragraphs[0].Append(idx.ToString()).Alignment = Alignment.center;
                    row.Cells[1].Paragraphs[0].Append(q.QuestionText ?? "—");

                    // Получаем ID вариантов, которые отметил студент по этому вопросу
                    studentAnswers.TryGetValue(q.Id, out var selectedOptionIds);
                    selectedOptionIds ??= new List<int>();

                    // 2. ВЫВОДИМ ОТВЕТЫ СТУДЕНТА ПООЧЕРЕДНО
                    var answerCell = row.Cells[2]; // Индекс 3 — четвертая колонка "Ответ слушателя"
                    var firstParagraph = answerCell.Paragraphs[0];

                    // Очищаем дефолтный пустой текст/пробел, который Word создает по умолчанию в новой ячейке
                    if (firstParagraph.Text.Length > 0)
                    {
                        firstParagraph.RemoveText(0, firstParagraph.Text.Length);
                    }

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
                                if (currentOptIdx == 0)
                                {
                                    // В самый первый (очищенный) абзац ячейки текст добавляем через Append
                                    firstParagraph.Append($"• {option.AnswerText}");
                                }
                                else
                                {
                                    // Последующие ответы добавляем новыми абзацами друг за другом построчно
                                    answerCell.InsertParagraph($"• {option.AnswerText}");
                                }
                                currentOptIdx++;
                            }
                        }
                    }



                    // 3. ПРОВЕРЯЕМ ПРАВИЛЬНОСТЬ (Честное сравнение списков)
                    var correctOptionIds = q.Answers.Where(a => a.IsCorrect).Select(a => a.Id).ToList();
                    bool isUserCorrect = correctOptionIds.Count == selectedOptionIds.Count && !correctOptionIds.Except(selectedOptionIds).Any();

                    row.Cells[3].Paragraphs[0].Append(isUserCorrect ? "Верно" : "Неверно").Alignment = Alignment.center;

                    idx++;
                }

                table.SetWidths(new float[] { 40f, 260f, 200f, 90f }); ;
                doc.InsertTable(table);

                doc.InsertParagraph("\n"); // Небольшой отступ от таблицы

                // Считаем количество ошибок
                int totalQuestionsCount = attempt.TotalQuestionsCount > 0 ? attempt.TotalQuestionsCount : questions.Count;
                int errorsCount = totalQuestionsCount - attempt.CorrectAnswersCount;
                if (errorsCount < 0) errorsCount = 0;

                // Считаем проценты
                int errorsPercentage = totalQuestionsCount > 0 ? (int)Math.Round((double)errorsCount / totalQuestionsCount * 100) : 0;
                
                // Допустимое количество ошибок (на основе PassPercentage, например, если порог 70%, то допустимо 30% ошибок)
                int allowedErrorsPercentage = 100 - attempt.CourseMaterial!.PassPercentage;
                int allowedErrorsCount = (int)Math.Floor((double)totalQuestionsCount * allowedErrorsPercentage / 100);

                // Выводим строки итогов
                doc.InsertParagraph($"Итого вопросов: {totalQuestionsCount}");
                doc.InsertParagraph($"Допустимое количество ошибок: {allowedErrorsCount} ({allowedErrorsPercentage} %)");
                doc.InsertParagraph($"Допущено ошибок: {errorsCount} ({errorsPercentage} %)");
                doc.InsertParagraph($"Верных ответов: {attempt.CorrectAnswersCount} ({attempt.ScorePercentage} %)");

                doc.InsertParagraph("\n"); // Отступ

                // РЕЗУЛЬТАТ ТЕСТИРОВАНИЯ (Крупно и жирно)
                string resultStatusText = attempt.Status == AttemptStatus.Passed ? "СДАНО" : "НЕ СДАНO";
                var pFinalResult = doc.InsertParagraph("Результат тестирования: ");
                pFinalResult.Append(resultStatusText).Bold();
                pFinalResult.FontSize(12);

                doc.InsertParagraph("\n");

                // Текст об отсутствии нарушений
                doc.InsertParagraph("При проведении тестирования нарушений его порядка не зафиксировано.");

                doc.InsertParagraph("\n\n"); // Отступы перед блоком подписей

                // --- БЛОК ПОДПИСЕЙ СТОРОН ---
                // 1. Ответственный за проведение
                var pSignAdmin = doc.InsertParagraph("Ответственный за проведение тестирования  _________________ / _________________________________");
                pSignAdmin.SpacingAfter(25); // Отступ между подписями

                // 2. Тестируемый (Выводим Фамилию И.О. студента автоматически)
                string shortStudentName = $"{attempt.Student?.LastName} {attempt.Student?.FirstName.FirstOrDefault()}.{attempt.Student?.MiddleName?.FirstOrDefault()}.";
                doc.InsertParagraph($"Тестируемый  _________________ / {shortStudentName}");

                // Сохраняем структуру документа в поток памяти
                doc.Save();
            }

            string fileName = $"Protocol_{attempt.Id}_{attempt.Student?.LastName}.docx";
            return File(memoryStream.ToArray(), "application/vnd.openxmlformats-officedocument.wordprocessingml.document", fileName);
        }
    }

    // POST: /StudentTest/SubmitAnswers
    // Фиксация ответов, сохранение слепка JSON и закрытие попытки
    [HttpPost]
    public async Task<IActionResult> SubmitAnswers([FromBody] TestSubmissionDto model)
    {
        if (model == null) return Json(new { success = false, message = "Некорректные данные!" });

        // Извлекаем ID из твоей системы авторизации (AuthController)
        var userIdClaim = User.FindFirst("UserId")?.Value;
        if (userIdClaim == null) return Json(new { success = false, message = "Вы не авторизованы!" });
        int studentId = int.Parse(userIdClaim);

        var testMaterial = await _context.CourseMaterials.FindAsync(model.TestId);
        if (testMaterial == null || testMaterial.Type != MaterialType.Test)
            return Json(new { success = false, message = "Тест не найден!" });

        // Ищем строго текущую открытую попытку
        var attempt = await _context.TestAttempts
            .FirstOrDefaultAsync(a => a.CourseMaterialId == model.TestId && a.StudentId == studentId && a.Status == AttemptStatus.InProgress);

        if (attempt == null)
            return Json(new { success = false, message = "Активная сессия тестирования не найдена или уже завершена по таймауту." });

        // Проверка времени сервером
        if (attempt.HardDeadline.HasValue && DateTime.UtcNow > attempt.HardDeadline.Value)
        {
            attempt.Status = AttemptStatus.Failed;
            attempt.FinishedAt = attempt.HardDeadline;
            await _context.SaveChangesAsync();
            return Json(new { success = false, message = "⏱️ Время тестирования истекло! Результат не засчитан." });
        }

        var answeredQuestionIds = model.StudentAnswers.Keys.ToList();
        var questions = await _context.TestQuestions
            .Include(q => q.Answers)
            .Where(q => answeredQuestionIds.Contains(q.Id))
            .ToListAsync();

        int correctQuestionsCount = 0;

        foreach (var q in questions)
        {
            model.StudentAnswers.TryGetValue(q.Id, out var selectedAnswerIds);
            selectedAnswerIds ??= new List<int>();
            var correctOptionIds = q.Answers.Where(a => a.IsCorrect).Select(a => a.Id).ToList();

            bool isUserCorrect = correctOptionIds.Count == selectedAnswerIds.Count && !correctOptionIds.Except(selectedAnswerIds).Any();
            if (isUserCorrect) correctQuestionsCount++;
        }

        int totalQuestions = attempt.TotalQuestionsCount > 0 ? attempt.TotalQuestionsCount : questions.Count;
        int finalScorePercentage = totalQuestions > 0 ? (int)Math.Round((double)correctQuestionsCount / totalQuestions * 100) : 0;

        bool isPassed = finalScorePercentage >= testMaterial.PassPercentage;

        // Фиксируем результаты и закрываем попытку
        attempt.ScorePercentage = finalScorePercentage;
        attempt.CorrectAnswersCount = correctQuestionsCount;
        attempt.TotalQuestionsCount = totalQuestions;
        attempt.Status = isPassed ? AttemptStatus.Passed : AttemptStatus.Failed;
        attempt.FinishedAt = DateTime.UtcNow;

        // КРИТИЧЕСКИ ВАЖНО: Сохраняем слепок ответов студента для корректного DOCX!
        attempt.StudentAnswersJson = System.Text.Json.JsonSerializer.Serialize(model.StudentAnswers);

        await _context.SaveChangesAsync();

        // Пересчитываем общую успеваемость по курсу через GradeService
        await GradeService.RecalculateCourseProgressAsync(_context, studentId, testMaterial.CourseId);

        // Динамический URL редиректа на страницу плеера курса
        string returnUrl = $"/CourseMaterials/StudentView?courseId={testMaterial.CourseId}&activeId={testMaterial.Id}";

        return Json(new
        {
            success = true,
            score = finalScorePercentage,
            requiredScore = testMaterial.PassPercentage,
            isPassed = isPassed,
            redirectUrl = returnUrl,
            message = isPassed ? "🎉 Поздравляем! Тест успешно сдан." : "❌ К сожалению, вы не набрали проходной балл."
        });
    }
}


public class TestSubmissionDto
{
    public int TestId { get; set; }
    public Dictionary<int, List<int>> StudentAnswers { get; set; } = new();
}
