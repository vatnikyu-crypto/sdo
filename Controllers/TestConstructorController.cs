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
    private readonly IWebHostEnvironment _env;

    // Внедряем IWebHostEnvironment для работы с путями загрузки файлов
    public TestConstructorController(AppDbContext context, IWebHostEnvironment env)
    {
        _context = context;
        _env = env;
    }

    // GET: /TestConstructor/Index?testId=5
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
    // Добавлен параметр string? hint и список файлов для картинок ответов
    [HttpPost]
    public async Task<IActionResult> Create(int testId, string questionText, string? hint, List<string> answers, List<int> correctIndices, IFormFile?[] answerImages)
    {
        if (string.IsNullOrWhiteSpace(questionText))
            return Json(new { success = false, message = "Текст вопроса не может быть пустым!" });

        if (answers == null || answers.Count < 2 || answers.Any(string.IsNullOrWhiteSpace))
            return Json(new { success = false, message = "Добавьте минимум 2 заполненных варианта ответа!" });

        if (correctIndices == null || !correctIndices.Any())
            return Json(new { success = false, message = "Укажите хотя бы один правильный ответ!" });

        try
        {
            var newQuestion = new TestQuestion
            {
                CourseMaterialId = testId,
                QuestionText = questionText.Trim(),
                Hint = string.IsNullOrWhiteSpace(hint) ? null : hint.Trim() // Пишем подсказку
            };

            _context.TestQuestions.Add(newQuestion);
            await _context.SaveChangesAsync();

            for (int i = 0; i < answers.Count; i++)
            {
                string? savedImageUrl = null;

                // Проверяем, загружена ли картинка для текущего индекса ответа
                if (answerImages != null && i < answerImages.Length && answerImages[i] != null && answerImages[i]!.Length > 0)
                {
                    savedImageUrl = await SaveOptionImageAsync(answerImages[i]!);
                }

                var option = new TestAnswerOption
                {
                    TestQuestionId = newQuestion.Id,
                    AnswerText = answers[i].Trim(),
                    IsCorrect = correctIndices.Contains(i),
                    ImageUrl = savedImageUrl // Пишем путь к картинке
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
    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var question = await _context.TestQuestions.FindAsync(id);
        if (question == null) return Json(new { success = false, message = "Вопрос не найден!" });

        try
        {
            var options = await _context.TestAnswerOptions.Where(o => o.TestQuestionId == id).ToListAsync();

            // Физически стираем файлы картинок ответов при удалении вопроса
            foreach (var opt in options)
            {
                DeleteOptionImage(opt.ImageUrl);
            }

            _context.TestAnswerOptions.RemoveRange(options);
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
    [HttpGet]
    public async Task<IActionResult> GetQuestionData(int id)
    {
        var question = await _context.TestQuestions
            .Include(q => q.Answers)
            .FirstOrDefaultAsync(q => q.Id == id);

        if (question == null) return NotFound();

        return Json(new
        {
            id = question.Id,
            text = question.QuestionText,
            hint = question.Hint ?? "", // Отдаем подсказку на фронт
            answers = question.Answers.Select(a => new
            {
                text = a.AnswerText,
                isCorrect = a.IsCorrect,
                imageUrl = a.ImageUrl ?? "" // Отдаем путь к картинке на фронт
            })
        });
    }

    // POST: /TestConstructor/Edit
    [HttpPost]
    public async Task<IActionResult> Edit(int id, string questionText, string? hint, List<string> answers, List<int> correctIndices, IFormFile?[] answerImages, List<string?> existingImageUrls)
    {
        var question = await _context.TestQuestions.FindAsync(id);
        if (question == null) return Json(new { success = false, message = "Вопрос не найден!" });

        try
        {
            // 1. Обновляем базовые поля вопроса
            question.QuestionText = questionText.Trim();
            question.Hint = string.IsNullOrWhiteSpace(hint) ? null : hint.Trim();
            _context.TestQuestions.Update(question);

            // 2. Вытаскиваем старые варианты для очистки физических файлов
            var oldOptions = await _context.TestAnswerOptions.Where(o => o.TestQuestionId == id).ToListAsync();

            // 3. Записываем новый измененный массив ответов
            for (int i = 0; i < answers.Count; i++)
            {
                string? finalImageUrl = null;

                // Проверяем, прилетела ли НОВАЯ картинка из инпута
                if (answerImages != null && i < answerImages.Length && answerImages[i] != null && answerImages[i]!.Length > 0)
                {
                    finalImageUrl = await SaveOptionImageAsync(answerImages[i]!);
                }
                // Если новой картинки нет, проверяем, осталась ли старая (при редактировании строк)
                else if (existingImageUrls != null && i < existingImageUrls.Count && !string.IsNullOrEmpty(existingImageUrls[i]))
                {
                    finalImageUrl = existingImageUrls[i];
                }

                // Логика удаления файлов, которые админ стер или заменил
                if (oldOptions.Any(o => o.AnswerText == answers[i] && o.ImageUrl != finalImageUrl))
                {
                    var fileToClear = oldOptions.FirstOrDefault(o => o.AnswerText == answers[i] && o.ImageUrl != finalImageUrl);
                    if (fileToClear != null) DeleteOptionImage(fileToClear.ImageUrl);
                }

                var option = new TestAnswerOption
                {
                    TestQuestionId = id,
                    AnswerText = answers[i].Trim(),
                    IsCorrect = correctIndices.Contains(i),
                    ImageUrl = finalImageUrl
                };
                _context.TestAnswerOptions.Add(option);
            }

            // Удаляем старые строчки опций из БД, заменяя их новыми
            _context.TestAnswerOptions.RemoveRange(oldOptions);
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Ошибка обновления: {ex.Message}" });
        }
    }

    // ПОСТ: /TestConstructor/DeleteMultiple
    // Массовое асинхронное удаление выбранных вопросов (с очисткой физических файлов картинок)
    [HttpPost]
    public async Task<IActionResult> DeleteMultiple([FromBody] List<int> ids)
    {
        if (ids == null || !ids.Any())
        {
            return Json(new { success = false, message = "Не выбрано ни одного вопроса!" });
        }

        try
        {
            // 1. Находим все варианты ответов для этих вопросов, чтобы стереть их картинки
            var optionsToDelete = await _context.TestAnswerOptions
                .Where(o => ids.Contains(o.TestQuestionId))
                .ToListAsync();

            // Физически стираем файлы картинок для всех удаляемых вариантов ответов
            foreach (var opt in optionsToDelete)
            {
                DeleteOptionImage(opt.ImageUrl);
            }

            // 2. Находим сами вопросы
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
    // Экспорт выбранных вопросов в формат CSV (теперь с поддержкой Подсказок)
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
        // ДОБАВИЛИ КОЛОНКУ Hint В ЗАГОЛОВОК CSV
        csvBuilder.AppendLine("QuestionText;Hint;Answers;CorrectFlags");

        foreach (var q in questions)
        {
            // Защищаем текст вопроса и подсказки от точек с запятой, ломающих CSV структуру
            var qText = q.QuestionText.Replace(";", " ");
            var qHint = (q.Hint ?? "").Replace(";", " ");

            // Склеиваем тексты ответов через тильду '~', экранируя точку с запятой
            var answersStr = string.Join("~", q.Answers.Select(a => a.AnswerText.Replace(";", " ")));
            // Склеиваем флаги правильности (1 - тру, 0 - фолс) через тильду '~'
            var flagsStr = string.Join("~", q.Answers.Select(a => a.IsCorrect ? "1" : "0"));

            // Сохраняем строку в файл с новой колонкой подсказки
            csvBuilder.AppendLine($"{qText};{qHint};{answersStr};{flagsStr}");
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(csvBuilder.ToString());
        // Добавляем BOM для корректного отображения кириллицы в Excel
        var resultBytes = System.Text.Encoding.UTF8.GetPreamble().Concat(bytes).ToArray();

        return File(resultBytes, "text/csv", $"export_questions_{DateTime.Now:yyyyMMddHHmmss}.csv");
    }

    // ПОСТ: /TestConstructor/ImportFromCsv
    // Импорт вопросов из CSV файла (с поддержкой подсказок Hint)
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
                    // Теперь должно быть минимум 4 колонки: Текст;Подсказка;Ответы;Флаги
                    if (parts.Length < 4)
                    {
                        errorCount++;
                        errors.Add($"Строка {lineNum}: Неверный формат (не хватает колонок, должно быть 4).");
                        continue;
                    }

                    string qText = parts[0].Trim();
                    string qHint = parts[1].Trim();
                    string[] answers = parts[2].Split('~');
                    string[] flags = parts[3].Split('~');

                    if (string.IsNullOrEmpty(qText) || answers.Length < 2 || answers.Length != flags.Length)
                    {
                        errorCount++;
                        errors.Add($"Строка {lineNum}: Ошибка валидации данных вопроса или вариантов ответов.");
                        continue;
                    }

                    // Создаем вопрос с поддержкой подсказки
                    var question = new TestQuestion
                    {
                        CourseMaterialId = testId,
                        QuestionText = qText,
                        Hint = string.IsNullOrWhiteSpace(qHint) ? null : qHint
                    };
                    _context.TestQuestions.Add(question);
                    await _context.SaveChangesAsync();

                    // Добавляем варианты ответов (картинки при импорте текста из CSV остаются null)
                    for (int i = 0; i < answers.Length; i++)
                    {
                        _context.TestAnswerOptions.Add(new TestAnswerOption
                        {
                            TestQuestionId = question.Id,
                            AnswerText = answers[i].Trim(),
                            IsCorrect = flags[i].Trim() == "1",
                            ImageUrl = null
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

    // ХЕЛПЕР: Сохранение картинок вариантов ответов на сервер
    private async Task<string> SaveOptionImageAsync(IFormFile file)
    {
        string extension = Path.GetExtension(file.FileName).ToLower();
        string folder = Path.Combine(_env.WebRootPath, "uploads", "materials", "test_options");
        if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

        string uniqueName = Guid.NewGuid().ToString() + extension;
        string fullPath = Path.Combine(folder, uniqueName);

        using (var stream = new FileStream(fullPath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }
        return $"/uploads/materials/test_options/{uniqueName}";
    }

    // ХЕЛПЕР: Физическое удаление файлов картинок с диска
    private void DeleteOptionImage(string? relativePath)
    {
        if (!string.IsNullOrEmpty(relativePath) && relativePath.StartsWith("/uploads/"))
        {
            string absPath = Path.Combine(_env.WebRootPath, relativePath.TrimStart('/'));
            if (System.IO.File.Exists(absPath)) System.IO.File.Delete(absPath);
        }
    }
}
