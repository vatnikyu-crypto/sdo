using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SdoApp.Controllers;

[Authorize(Roles = "Админ")] // Доступ только для Администратора
public class TestConstructorController : Controller
{
    private readonly AppDbContext _context;

    public TestConstructorController(AppDbContext context)
    {
        _context = context;
    }

    // GET: /TestConstructor/Index?testId=5
    // Страница со списком всех вопросов данного теста
    [HttpGet]
    public async Task<IActionResult> Index(int testId)
    {
        var testMaterial = await _context.CourseMaterials
            .Include(m => m.Course)
            .FirstOrDefaultAsync(m => m.Id == testId);

        if (testMaterial == null || testMaterial.Type != MaterialType.Test)
            return NotFound();

        var questions = await _context.TestQuestions
            .Include(q => q.Answers)
            .Where(q => q.CourseMaterialId == testId)
            .ToListAsync();

        ViewBag.TestId = testMaterial.Id;
        ViewBag.TestTitle = testMaterial.Title;
        ViewBag.CourseId = testMaterial.CourseId;
        ViewBag.CourseTitle = testMaterial.Course?.Title;

        return View(questions);
    }

    // POST: /TestConstructor/Create
    // Обновленный метод: принимает ЛЮБОЕ количество ответов и ЛЮБОЕ количество правильных вариантов
    [HttpPost]
    public async Task<IActionResult> Create(int testId, string questionText, List<string> answers, List<int> correctIndices)
    {
        if (string.IsNullOrWhiteSpace(questionText))
        {
            return Json(new { success = false, message = "Текст вопроса не может быть пустым!" });
        }

        if (answers == null || answers.Count < 2 || answers.Any(string.IsNullOrWhiteSpace))
        {
            return Json(new { success = false, message = "Добавьте как минимум 2 заполненных варианта ответа!" });
        }

        if (correctIndices == null || !correctIndices.Any())
        {
            return Json(new { success = false, message = "Укажите хотя бы один правильный вариант ответа!" });
        }

        try
        {
            var newQuestion = new TestQuestion
            {
                CourseMaterialId = testId,
                QuestionText = questionText.Trim()
            };

            _context.TestQuestions.Add(newQuestion);
            await _context.SaveChangesAsync();

            // Проходим циклом по всем присланным ответам (их может быть 3, 4, 5, 6...)
            for (int i = 0; i < answers.Count; i++)
            {
                var option = new TestAnswerOption
                {
                    TestQuestionId = newQuestion.Id,
                    AnswerText = answers[i].Trim(),
                    // Если индекс текущего ответа есть в списке правильных — помечаем как true
                    IsCorrect = correctIndices.Contains(i)
                };
                _context.TestAnswerOptions.Add(option);
            }

            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Ошибка бэкенда: {ex.Message}" });
        }
    }

    // POST: /TestConstructor/Delete/5
    // Асинхронное удаление вопроса (каскадно удалит и варианты ответов)
    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var question = await _context.TestQuestions.FindAsync(id);
        if (question == null)
        {
            return Json(new { success = false, message = "Вопрос не найден!" });
        }

        try
        {
            // Удаляем варианты ответов, привязанные к вопросу
            var options = await _context.TestAnswerOptions.Where(o => o.TestQuestionId == id).ToListAsync();
            _context.TestAnswerOptions.RemoveRange(options);

            // Удаляем сам вопрос
            _context.TestQuestions.Remove(question);
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Ошибка при удалении: {ex.Message}" });
        }
    }

    // GET: /TestConstructor/GetQuestionData/5
    // Возвращает JSON со всеми ответами вопроса для динамической сборки формы изменения
    [HttpGet]
    public async Task<IActionResult> GetQuestionData(int id)
    {
        var question = await _context.TestQuestions
            .Include(q => q.Answers)
            .FirstOrDefaultAsync(q => q.Id == id);

        if (question == null) return NotFound();

        // Формируем чистый JSON-пакет
        return Json(new
        {
            id = question.Id,
            text = question.QuestionText,
            answers = question.Answers.Select(a => new
            {
                text = a.AnswerText,
                isCorrect = a.IsCorrect
            })
        });
    }

    // POST: /TestConstructor/Edit
    // Полностью перезаписывает вопрос и варианты ответов новыми данными
    [HttpPost]
    public async Task<IActionResult> Edit(int id, string questionText, List<string> answers, List<int> correctIndices)
    {
        var question = await _context.TestQuestions.FindAsync(id);
        if (question == null) return Json(new { success = false, message = "Вопрос не найден!" });

        if (string.IsNullOrWhiteSpace(questionText)) return Json(new { success = false, message = "Текст вопроса не может быть пустым!" });

        if (answers == null || answers.Count < 2 || answers.Any(string.IsNullOrWhiteSpace))
            return Json(new { success = false, message = "Добавьте как минимум 2 заполненных варианта ответа!" });

        if (correctIndices == null || !correctIndices.Any())
            return Json(new { success = false, message = "Укажите хотя бы один правильный вариант ответа!" });

        try
        {
            // 1. Обновляем текст самого вопроса
            question.QuestionText = questionText.Trim();
            _context.TestQuestions.Update(question);

            // 2. Сносим старые варианты ответов этого вопроса из БД (каскадная замена)
            var oldOptions = await _context.TestAnswerOptions.Where(o => o.TestQuestionId == id).ToListAsync();
            _context.TestAnswerOptions.RemoveRange(oldOptions);
            await _context.SaveChangesAsync();

            // 3. Записываем новый измененный массив ответов
            for (int i = 0; i < answers.Count; i++)
            {
                var option = new TestAnswerOption
                {
                    TestQuestionId = id,
                    AnswerText = answers[i].Trim(),
                    IsCorrect = correctIndices.Contains(i)
                };
                _context.TestAnswerOptions.Add(option);
            }

            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Ошибка при обновлении: {ex.Message}" });
        }
    }

    // ПОСТ: /TestConstructor/DeleteMultiple
    // Массовое асинхронное удаление выбранных вопросов (и их ответов)
    [HttpPost]
    public async Task<IActionResult> DeleteMultiple([FromBody] List<int> ids)
    {
        if (ids == null || !ids.Any())
        {
            return Json(new { success = false, message = "Не выбрано ни одного вопроса!" });
        }

        try
        {
            // Находим все варианты ответов для этих вопросов
            var optionsToDelete = await _context.TestAnswerOptions
                .Where(o => ids.Contains(o.TestQuestionId))
                .ToListAsync();

            // Находим сами вопросы
            var questionsToDelete = await _context.TestQuestions
                .Where(q => ids.Contains(q.Id))
                .ToListAsync();

            _context.TestAnswerOptions.RemoveRange(optionsToDelete);
            _context.TestQuestions.RemoveRange(questionsToDelete);

            await _context.SaveChangesAsync();
            return Json(new { success = true, message = $"Успешно удалено вопросов: {questionsToDelete.Count}" });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Ошибка массового удаления: {ex.Message}" });
        }
    }

    // ПОСТ: /TestConstructor/ExportToCsv
    // Экспорт выбранных вопросов в формат CSV
    [HttpPost]
    public async Task<IActionResult> ExportToCsv([FromForm] string questionIdsJson)
    {
        if (string.IsNullOrEmpty(questionIdsJson)) return BadRequest("Не выбраны вопросы.");

        var ids = System.Text.Json.JsonSerializer.Deserialize<List<int>>(questionIdsJson);
        if (ids == null || !ids.Any()) return BadRequest("Список вопросов пуст.");

        var questions = await _context.TestQuestions
            .Include(q => q.Answers)
            .Where(q => ids.Contains(q.Id))
            .ToListAsync();

        var csvBuilder = new System.Text.StringBuilder();
        // Заголовок CSV (используем точку с запятой как в Users)
        csvBuilder.AppendLine("QuestionText;Answers;CorrectFlags");

        foreach (var q in questions)
        {
            // Склеиваем тексты ответов через тильду '~', экранируя точку с запятой
            var answersStr = string.Join("~", q.Answers.Select(a => a.AnswerText.Replace(";", " ")));
            // Склеиваем флаги правильности (1 - тру, 0 - фолс) через тильду '~'
            var flagsStr = string.Join("~", q.Answers.Select(a => a.IsCorrect ? "1" : "0"));

            csvBuilder.AppendLine($"{q.QuestionText.Replace(";", " ")};{answersStr};{flagsStr}");
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(csvBuilder.ToString());
        // Добавляем BOM для корректного отображения кириллицы в Excel
        var resultBytes = System.Text.Encoding.UTF8.GetPreamble().Concat(bytes).ToArray();

        return File(resultBytes, "text/csv", $"export_questions_{DateTime.Now:yyyyMMddHHmmss}.csv");
    }

    // ПОСТ: /TestConstructor/ImportFromCsv
    // Импорт вопросов из CSV файла
    [HttpPost]
    public async Task<IActionResult> ImportFromCsv(int testId, IFormFile csvFile)
    {
        if (csvFile == null || csvFile.Length == 0)
        {
            return Json(new { success = false, message = "Файл не выбран или пуст!" });
        }

        int successCount = 0;
        int errorCount = 0;
        var errors = new List<string>();

        try
        {
            using (var reader = new StreamReader(csvFile.OpenReadStream(), System.Text.Encoding.UTF8))
            {
                // Читаем заголовок асинхронно
                string? header = await reader.ReadLineAsync();
                int lineNum = 1;
                string? line;

                // Читаем строки до тех пор, пока ReadLineAsync() не вернет null
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    lineNum++;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var parts = line.Split(';');
                    if (parts.Length < 3)
                    {
                        errorCount++;
                        errors.Add($"Строка {lineNum}: Неверный формат (не хватает колонок).");
                        continue;
                    }

                    string qText = parts[0].Trim();
                    string[] answers = parts[1].Split('~');
                    string[] flags = parts[2].Split('~');

                    if (string.IsNullOrEmpty(qText) || answers.Length < 2 || answers.Length != flags.Length)
                    {
                        errorCount++;
                        errors.Add($"Строка {lineNum}: Ошибка валидации данных вопроса или вариантов ответов.");
                        continue;
                    }

                    // Создаем вопрос
                    var question = new TestQuestion
                    {
                        CourseMaterialId = testId,
                        QuestionText = qText
                    };
                    _context.TestQuestions.Add(question);
                    await _context.SaveChangesAsync();

                    // Добавляем варианты ответов
                    for (int i = 0; i < answers.Length; i++)
                    {
                        _context.TestAnswerOptions.Add(new TestAnswerOption
                        {
                            TestQuestionId = question.Id,
                            AnswerText = answers[i].Trim(),
                            IsCorrect = flags[i].Trim() == "1"
                        });
                    }
                    await _context.SaveChangesAsync();
                    successCount++;
                }
            }

            return Json(new { success = true, successCount, errorCount, errors });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Критическая ошибка при импорте: {ex.Message}" });
        }
    }
}
