using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models;
using ClosedXML.Excel;
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
    [HttpPost]
    public async Task<IActionResult> Create(int testId, string questionText, string? hint, bool isMultipleChoice, List<string> answers, List<int> correctIndices, IFormFile?[] answerImages)
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
                Hint = string.IsNullOrWhiteSpace(hint) ? null : hint.Trim(),
                IsMultipleChoice = isMultipleChoice // <--- СОХРАНЯЕМ ТИП
            };

            _context.TestQuestions.Add(newQuestion);
            await _context.SaveChangesAsync();

            for (int i = 0; i < answers.Count; i++)
            {
                string? savedImageUrl = null;
                if (answerImages != null && i < answerImages.Length && answerImages[i] != null && answerImages[i]!.Length > 0)
                {
                    savedImageUrl = await SaveOptionImageAsync(answerImages[i]!);
                }

                var option = new TestAnswerOption
                {
                    TestQuestionId = newQuestion.Id,
                    AnswerText = answers[i].Trim(),
                    IsCorrect = correctIndices.Contains(i),
                    ImageUrl = savedImageUrl
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
            hint = question.Hint ?? "",
            isMultipleChoice = question.IsMultipleChoice, // <--- ОТДАЕМ ТИП НА ФРОНТ
            answers = question.Answers.Select(a => new
            {
                text = a.AnswerText,
                isCorrect = a.IsCorrect,
                imageUrl = a.ImageUrl ?? ""
            })
        });
    }

    // POST: /TestConstructor/Edit
    [HttpPost]
    public async Task<IActionResult> Edit(int id, string questionText, string? hint, bool isMultipleChoice, List<string> answers, List<int> correctIndices, IFormFile?[] answerImages, List<string?> existingImageUrls)
    {
        var question = await _context.TestQuestions.FindAsync(id);
        if (question == null) return Json(new { success = false, message = "Вопрос не найден!" });

        try
        {
            question.QuestionText = questionText.Trim();
            question.Hint = string.IsNullOrWhiteSpace(hint) ? null : hint.Trim();
            question.IsMultipleChoice = isMultipleChoice; // <--- ОБНОВЛЯЕМ ТИП
            _context.TestQuestions.Update(question);

            var oldOptions = await _context.TestAnswerOptions.Where(o => o.TestQuestionId == id).ToListAsync();

            for (int i = 0; i < answers.Count; i++)
            {
                string? finalImageUrl = null;
                if (answerImages != null && i < answerImages.Length && answerImages[i] != null && answerImages[i]!.Length > 0)
                {
                    finalImageUrl = await SaveOptionImageAsync(answerImages[i]!);
                }
                else if (existingImageUrls != null && i < existingImageUrls.Count && !string.IsNullOrEmpty(existingImageUrls[i]))
                {
                    finalImageUrl = existingImageUrls[i];
                }

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


        // ПОСТ: /TestConstructor/ExportToExcel
    // Экспорт в формате 5 колонок (каждый ответ на новой строке)
    [HttpPost]
    public async Task<IActionResult> ExportToExcel([FromForm] string questionIdsJson)
    {
        if (string.IsNullOrEmpty(questionIdsJson)) return BadRequest("Не выбраны вопросы.");

        var ids = System.Text.Json.JsonSerializer.Deserialize<List<int>>(questionIdsJson);
        if (ids == null || !ids.Any()) return BadRequest("Список вопросов пуст.");

        var questions = await _context.TestQuestions
            .Include(q => q.Answers)
            .Where(q => ids.Contains(q.Id))
            .ToListAsync();

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Вопросы");

        // Заголовки как на скриншоте
        worksheet.Cell(1, 1).Value = "Вопрос";
        worksheet.Cell(1, 2).Value = "Комментарий к правильному ответу";
        worksheet.Cell(1, 3).Value = "Тип вопроса";
        worksheet.Cell(1, 4).Value = "Ответ";
        worksheet.Cell(1, 5).Value = "Ответ верен";

        // Оформление шапки
        var headerRange = worksheet.Range(1, 1, 1, 5);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#F0F4FF");
        headerRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        headerRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;

        int row = 2;
        foreach (var q in questions)
        {
            string typeStr = q.IsMultipleChoice ? "several_answers" : "one_answer";
            
            foreach (var ans in q.Answers)
            {
                worksheet.Cell(row, 1).Value = q.QuestionText;
                worksheet.Cell(row, 2).Value = q.Hint ?? "";
                worksheet.Cell(row, 3).Value = typeStr;
                worksheet.Cell(row, 4).Value = ans.AnswerText;
                worksheet.Cell(row, 5).Value = ans.IsCorrect ? "True" : "";
                row++;
            }
        }

        worksheet.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        string fileName = $"export-questions-{DateTime.Now:yyyy-MM-dd-HHmmss}.xlsx";
        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    // ПОСТ: /TestConstructor/ImportFromExcel
    // Импорт из Excel с поддержкой группировки строк
    [HttpPost]
    public async Task<IActionResult> ImportFromExcel(int testId, IFormFile excelFile)
    {
        if (excelFile == null || excelFile.Length == 0)
            return Json(new { success = false, message = "Файл не выбран или пуст!" });

        int successCount = 0;
        int errorCount = 0;
        var errors = new List<string>();

        try
        {
            using var stream = new MemoryStream();
            await excelFile.CopyToAsync(stream);
            using var workbook = new XLWorkbook(stream);
            var worksheet = workbook.Worksheet(1);

            var rows = worksheet.RowsUsed().Skip(1); // Пропускаем шапку
            
            TestQuestion? currentQuestion = null;
            string lastQuestionText = "";
            string lastHint = "";
            string lastType = "";

            foreach (var row in rows)
            {
                string qText = row.Cell(1).GetString().Trim();
                string hint = row.Cell(2).GetString().Trim();
                string type = row.Cell(3).GetString().Trim();
                string ansText = row.Cell(4).GetString().Trim();
                string isCorrectStr = row.Cell(5).GetString().Trim().ToLower();

                if (string.IsNullOrEmpty(qText)) continue; // Пропускаем пустые строки

                bool isCorrect = isCorrectStr == "true" || isCorrectStr == "1";

                // Если текст вопроса, подсказка или тип изменились — это НОВЫЙ вопрос
                if (qText != lastQuestionText || hint != lastHint || type != lastType || currentQuestion == null)
                {
                    currentQuestion = new TestQuestion
                    {
                        CourseMaterialId = testId,
                        QuestionText = qText,
                        Hint = string.IsNullOrWhiteSpace(hint) ? null : hint,
                        IsMultipleChoice = type == "several_answers"
                    };
                    _context.TestQuestions.Add(currentQuestion);
                    await _context.SaveChangesAsync();
                    
                    lastQuestionText = qText;
                    lastHint = hint;
                    lastType = type;
                    successCount++;
                }

                // Добавляем ответ к текущему вопросу
                if (currentQuestion != null && !string.IsNullOrEmpty(ansText))
                {
                    _context.TestAnswerOptions.Add(new TestAnswerOption
                    {
                        TestQuestionId = currentQuestion.Id,
                        AnswerText = ansText,
                        IsCorrect = isCorrect,
                        ImageUrl = null
                    });
                    await _context.SaveChangesAsync();
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
