using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models;
using SdoApp.Services;
using Xceed.Document.NET;
using Xceed.Words.NET;
using SdoApp.Models.DTO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SdoApp.Models.ViewModels;

namespace SdoApp.Controllers;

[Authorize(Roles = "Обучающийся")]
public class StudentTestController : Controller
{
    private readonly AppDbContext _context;

    public StudentTestController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<IActionResult> GetTestState(int materialId, int groupId)
    {
        int studentId = GetCurrentUserId();
        if (studentId == 0) return Json(new { success = false, message = "Не авторизован" });

        var material = await _context.CourseMaterials.FindAsync(materialId);
        if (material == null || material.Type != MaterialType.Test)
            return Json(new { success = false, message = "Тест не найден" });

        var attempts = await _context.TestAttempts
         .Where(a => a.CourseMaterialId == materialId && a.StudentId == studentId && a.GroupId == groupId)
         .OrderByDescending(a => a.StartedAt)
         .ToListAsync();

        var activeAttempt = attempts.FirstOrDefault(a => a.Status == AttemptStatus.InProgress);
        bool canContinue = false;
        int remainingSeconds = 0;

        if (activeAttempt != null)
        {
            if (activeAttempt.HardDeadline.HasValue)
            {
                if (DateTime.UtcNow < activeAttempt.HardDeadline.Value)
                {
                    canContinue = true;
                    remainingSeconds = (int)(activeAttempt.HardDeadline.Value - DateTime.UtcNow).TotalSeconds;
                }
                else
                {
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

    [HttpGet]
    public async Task<IActionResult> Take(int testId, int groupId) // ДОБАВЛЕН groupId
    {
        int studentId = GetCurrentUserId();
        if (studentId == 0) return Challenge();

        var testMaterial = await _context.CourseMaterials.FindAsync(testId);
        if (testMaterial == null || testMaterial.Type != MaterialType.Test)
            return NotFound("Тест не найден в системе.");

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

            DateTime? deadline = null;
            if (testMaterial.TimeLimit > 0)
            {
                deadline = DateTime.UtcNow.AddMinutes(testMaterial.TimeLimit);
            }

            activeAttempt = new TestAttempt
            {
                CourseMaterialId = testId,
                StudentId = studentId,
                GroupId = groupId,
                Status = AttemptStatus.InProgress,
                StartedAt = DateTime.UtcNow,
                HardDeadline = deadline
            };
            _context.TestAttempts.Add(activeAttempt);
            await _context.SaveChangesAsync();
        }
        else if (activeAttempt.HardDeadline.HasValue && DateTime.UtcNow > activeAttempt.HardDeadline.Value)
        {
            activeAttempt.Status = AttemptStatus.Failed;
            activeAttempt.FinishedAt = activeAttempt.HardDeadline;
            await _context.SaveChangesAsync();
            return RedirectToAction("Student", "Dashboard");
        }

        var finalQuestions = await _context.TestQuestions
            .Include(q => q.Answers)
            .Where(q => q.CourseMaterialId == testId)
            .ToListAsync();

        if (testMaterial.ShuffleQuestions && finalQuestions.Any())
        {
            var random = new Random(activeAttempt.Id);
            finalQuestions = finalQuestions.OrderBy(q => random.Next()).ToList();
        }

        if (testMaterial.QuestionsCountToUse > 0 && finalQuestions.Count > testMaterial.QuestionsCountToUse)
        {
            finalQuestions = finalQuestions.Take(testMaterial.QuestionsCountToUse).ToList();
        }

        if (activeAttempt.TotalQuestionsCount == 0)
        {
            activeAttempt.TotalQuestionsCount = finalQuestions.Count;
            await _context.SaveChangesAsync();
        }

        ViewBag.AttemptId = activeAttempt.Id;
        ViewBag.TestId = testMaterial.Id;
        ViewBag.TestTitle = testMaterial.Title;
        ViewBag.TestKind = testMaterial.TestKind;
        ViewBag.CourseId = testMaterial.CourseId;
        ViewBag.GroupId = groupId; // ДОБАВЛЕНО

        ViewBag.RemainingSeconds = activeAttempt.HardDeadline.HasValue
            ? (int)Math.Max(0, (activeAttempt.HardDeadline.Value - DateTime.UtcNow).TotalSeconds)
            : 0;

        var safeQuestionsDto = finalQuestions.Select(q => new StudentQuestionDto
        {
            Id = q.Id,
            QuestionText = q.QuestionText,
            Hint = q.Hint,
            IsMultipleChoice = q.IsMultipleChoice,
            Answers = q.Answers.Select(a => new StudentAnswerOptionDto
            {
                Id = a.Id,
                AnswerText = a.AnswerText,
                ImageUrl = a.ImageUrl
            }).ToList()
        }).ToList();

        return View(safeQuestionsDto);
    }

    [HttpPost]
    public async Task<IActionResult> SubmitAnswers([FromBody] TestSubmissionDto model)
    {
        if (model == null) return Json(new { success = false, message = "Некорректные данные!" });

        if (model.GroupId <= 0)
            return Json(new { success = false, message = "Ошибка: не передан ID группы." });

        var userIdClaim = User.FindFirst("UserId")?.Value;
        if (userIdClaim == null) return Json(new { success = false, message = "Вы не авторизованы!" });
        int studentId = int.Parse(userIdClaim);

        var testMaterial = await _context.CourseMaterials.FindAsync(model.TestId);

        // ДОБАВЛЕН groupId в returnUrl
        int courseId = testMaterial?.CourseId ?? model.CourseId;
        string returnUrl = $"/CourseMaterials/StudentView?courseId={courseId}&groupId={model.GroupId}&activeId={model.TestId}";

        if (testMaterial == null || testMaterial.Type != MaterialType.Test)
            return Json(new { success = false, message = "Тест не найден!", redirectUrl = returnUrl });

        var attempt = await _context.TestAttempts
            .FirstOrDefaultAsync(a => a.CourseMaterialId == model.TestId && a.StudentId == studentId && a.GroupId == model.GroupId && a.Status == AttemptStatus.InProgress);

        if (attempt == null)
            return Json(new { success = false, message = "Активная сессия тестирования не найдена или уже завершена по таймауту.", redirectUrl = returnUrl });

        if (attempt.HardDeadline.HasValue && DateTime.UtcNow > attempt.HardDeadline.Value)
        {
            attempt.Status = AttemptStatus.Failed;
            attempt.FinishedAt = attempt.HardDeadline;
            await _context.SaveChangesAsync();
            return Json(new { success = false, message = "⏱️ Время тестирования истекло! Результат не засчитан.", redirectUrl = returnUrl });
        }

        var answeredQuestionIds = model.StudentAnswers.Keys.ToList();
        var questions = await _context.TestQuestions
            .Include(q => q.Answers)
            .Where(q => q.CourseMaterialId == model.TestId)
            .ToListAsync();

        if (!questions.Any())
        {
            return Json(new { success = false, message = "В данном тесте отсутствуют вопросы. Обратитесь к администратору.", redirectUrl = returnUrl });
        }

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

        attempt.ScorePercentage = finalScorePercentage;
        attempt.CorrectAnswersCount = correctQuestionsCount;
        attempt.TotalQuestionsCount = totalQuestions;
        attempt.Status = isPassed ? AttemptStatus.Passed : AttemptStatus.Failed;
        attempt.FinishedAt = DateTime.UtcNow;
        attempt.StudentAnswersJson = System.Text.Json.JsonSerializer.Serialize(model.StudentAnswers);

        await _context.SaveChangesAsync();

        // ПЕРЕДАЕМ model.GroupId В СЕРВИС
        await GradeService.RecalculateCourseProgressAsync(_context, studentId, testMaterial.CourseId, model.GroupId);

        return Json(new
        {
            success = true,
            attemptId = attempt.Id,
            score = finalScorePercentage,
            requiredScore = testMaterial.PassPercentage,
            isPassed = isPassed,
            redirectUrl = $"/StudentTest/Result?attemptId={attempt.Id}",
            message = isPassed ? " Поздравляем! Тест успешно сдан." : " К сожалению, вы не набрали проходной балл."
        });
    }

        [HttpGet]
    public async Task<IActionResult> Result(int attemptId)
    {
        int studentId = GetCurrentUserId();
        if (studentId == 0) return Challenge();

        var attempt = await _context.TestAttempts
            .Include(a => a.CourseMaterial)
                .ThenInclude(m => m!.Course)
            .FirstOrDefaultAsync(a => a.Id == attemptId && a.StudentId == studentId);

        if (attempt == null || attempt.CourseMaterial == null)
            return NotFound("Результат теста не найден.");

        var material = attempt.CourseMaterial;

        var allQuestions = await _context.TestQuestions
            .Include(q => q.Answers)
            .Where(q => q.CourseMaterialId == material.Id)
            .ToListAsync();

        // Парсим ответы студента
        var studentAnswers = new Dictionary<int, List<int>>();
        if (!string.IsNullOrEmpty(attempt.StudentAnswersJson))
        {
            try
            {
                studentAnswers = System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<int, List<int>>>(attempt.StudentAnswersJson)
                    ?? new Dictionary<int, List<int>>();
            }
            catch { }
        }

        // Показываем ТОЛЬКО те вопросы, на которые студент дал хотя бы один ответ.
        // Это защищает правильные ответы на пропущенные вопросы от "утечки".
        IEnumerable<TestQuestion> questionsToShow;

        var answeredQuestionIds = studentAnswers
            .Where(kvp => kvp.Value != null && kvp.Value.Any())
            .Select(kvp => kvp.Key)
            .ToHashSet();

        if (answeredQuestionIds.Any())
        {
            questionsToShow = allQuestions.Where(q => answeredQuestionIds.Contains(q.Id)).ToList();
        }
        else
        {
            // Студент не дал ни одного ответа — разбор не нужен
            questionsToShow = Enumerable.Empty<TestQuestion>();
        }

        var vm = new TestResultViewModel
        {
            AttemptId = attempt.Id,
            GroupId = attempt.GroupId,
            TestTitle = material.Title,
            CourseTitle = material.Course?.Title ?? "",
            CourseId = material.CourseId,
            IsPassed = attempt.Status == AttemptStatus.Passed,
            ScorePercentage = attempt.ScorePercentage,
            PassPercentage = material.PassPercentage,
            CorrectAnswers = attempt.CorrectAnswersCount,
            TotalQuestions = attempt.TotalQuestionsCount > 0 ? attempt.TotalQuestionsCount : questionsToShow.Count(),
            StartedAt = attempt.StartedAt,
            FinishedAt = attempt.FinishedAt,
            Duration = attempt.FinishedAt.HasValue
                ? attempt.FinishedAt.Value - attempt.StartedAt
                : TimeSpan.Zero
        };

        int qNum = 0;
        foreach (var q in questionsToShow)
        {
            qNum++;
            studentAnswers.TryGetValue(q.Id, out var selectedIds);
            selectedIds ??= new List<int>();

            var correctIds = q.Answers.Where(a => a.IsCorrect).Select(a => a.Id).ToList();
            bool isCorrect = correctIds.Count == selectedIds.Count && !correctIds.Except(selectedIds).Any();

            vm.Questions.Add(new QuestionResultItem
            {
                Number = qNum,
                QuestionText = q.QuestionText,
                StudentAnswers = q.Answers.Where(a => selectedIds.Contains(a.Id)).Select(a => a.AnswerText).ToList(),
                CorrectAnswers = q.Answers.Where(a => a.IsCorrect).Select(a => a.AnswerText).ToList(),
                IsCorrect = isCorrect
            });
        }

        return View(vm);
    }

        // GET: /StudentTest/DownloadResult?attemptId
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

        var memoryStream = new MemoryStream();
        {
            using (var doc = DocX.Create(memoryStream))
            {

                doc.SetDefaultFont(new Xceed.Document.NET.Font("Times New Roman"), 12);

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
                var pSignAdmin = doc.InsertParagraph("Ответственный за проведение тестирования  _________________ / _________________");
                pSignAdmin.SpacingAfter(25); // Отступ между подписями

                // 2. Тестируемый (Выводим Фамилию И.О. студента автоматически)
                string shortStudentName = $"{attempt.Student?.LastName} {attempt.Student?.FirstName.FirstOrDefault()}.{attempt.Student?.MiddleName?.FirstOrDefault()}.";
                doc.InsertParagraph($"Тестируемый  _________________ / {shortStudentName}");

                // Сохраняем структуру документа в поток памяти
                doc.Save();
            }

            string fileName = $"Протокол_{attempt.Id}_{attempt.Student?.LastName}.docx";
            memoryStream.Position = 0;
            return File(memoryStream, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", fileName);
        }
    }
}