using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models;
using SdoApp.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SdoApp.Controllers;

[Authorize(Roles = "Обучающийся")]
public class StudentTestController : Controller
{
    private readonly AppDbContext _context;
    public StudentTestController(AppDbContext context) { _context = context; }

    // GET: /StudentTest/Take?testId=5
    [HttpGet]
    public async Task<IActionResult> Take(int testId)
    {
        var testMaterial = await _context.CourseMaterials.FindAsync(testId);
        if (testMaterial == null || testMaterial.Type != MaterialType.Test)
            return NotFound("Тест не найден в системе.");

        // 1. Сначала просто забираем все вопросы этого теста из SQLite в память приложения
        var finalQuestions = await _context.TestQuestions
            .Include(q => q.Answers)
            .Where(q => q.CourseMaterialId == testId)
            .ToListAsync();

        // 2. ИСПРАВЛЕНО: Применяем логику перемешивания в памяти (In-Memory)
        if (testMaterial.ShuffleQuestions && finalQuestions.Any())
        {
            var random = new Random();
            // Быстрая и безопасная сортировка по случайному числу средствами .NET
            finalQuestions = finalQuestions.OrderBy(q => random.Next()).ToList();
        }

        // 3. Применяем ограничение количества вопросов, если оно задано
        if (testMaterial.QuestionsCountToUse > 0 && finalQuestions.Count > testMaterial.QuestionsCountToUse)
        {
            finalQuestions = finalQuestions.Take(testMaterial.QuestionsCountToUse).ToList();
        }

        ViewBag.TestId = testMaterial.Id;
        ViewBag.TestTitle = testMaterial.Title;
        ViewBag.TimeLimit = testMaterial.TimeLimit;
        ViewBag.TestKind = testMaterial.TestKind;

        return View(finalQuestions);
    }

    // 2. ОБНОВЛЕННЫЙ МЕТОД ПРИЕМА ОТВЕТОВ СТУДЕНТА (ЧЕРЕЗ DTO-КЛАСС)
    // POST: /StudentTest/SubmitAnswers
    [HttpPost]
    public async Task<IActionResult> SubmitAnswers([FromBody] TestSubmissionDto model)
    {
        if (model == null) return Json(new { success = false, message = "Некорректные данные запроса!" });

        var userIdClaim = User.FindFirst("UserId")?.Value;
        if (userIdClaim == null) return Json(new { success = false, message = "Вы не авторизованы!" });
        int studentId = int.Parse(userIdClaim);

        var testMaterial = await _context.CourseMaterials.FindAsync(model.TestId);
        if (testMaterial == null || testMaterial.Type != MaterialType.Test)
            return Json(new { success = false, message = "Тест не найден!" });

        var questions = await _context.TestQuestions
            .Include(q => q.Answers)
            .Where(q => q.CourseMaterialId == model.TestId)
            .ToListAsync();

        int totalQuestions = questions.Count;
        int correctQuestionsCount = 0;

        foreach (var q in questions)
        {
            model.StudentAnswers.TryGetValue(q.Id, out var selectedAnswerIds);
            selectedAnswerIds ??= new List<int>();

            var correctOptionIds = q.Answers.Where(a => a.IsCorrect).Select(a => a.Id).ToList();

            bool isUserCorrect = correctOptionIds.Count == selectedAnswerIds.Count && !correctOptionIds.Except(selectedAnswerIds).Any();

            if (isUserCorrect)
            {
                correctQuestionsCount++;
            }
        }

        int finalScorePercentage = totalQuestions > 0 ? (int)Math.Round((double)correctQuestionsCount / totalQuestions * 100) : 0;

        var submission = new StudentSubmission
        {
            CourseMaterialId = model.TestId,
            StudentId = studentId,
            Grade = finalScorePercentage,
            SubmittedAt = DateTime.UtcNow,
            IsReviewed = true,
            StudentTextResponse = $"Результат теста: {correctQuestionsCount} из {totalQuestions} верных ответов ({finalScorePercentage}%)."
        };

        _context.StudentSubmissions.Add(submission);
        await _context.SaveChangesAsync();

        await GradeService.RecalculateCourseProgressAsync(_context, studentId, testMaterial.CourseId);

        bool isPassed = finalScorePercentage >= testMaterial.PassPercentage;
        return Json(new
        {
            success = true,
            score = finalScorePercentage,
            requiredScore = testMaterial.PassPercentage,
            isPassed = isPassed,
            message = isPassed ? "🎉 Поздравляем! Тест успешно сдан." : "❌ К сожалению, вы не набрали проходной балл."
        });
    }
}

// 3. ВСПОМОГАТЕЛЬНЫЙ DTO-КЛАСС ДЛЯ ПРАВИЛЬНОЙ ДЕСЕРИАЛИЗАЦИИ JSON
public class TestSubmissionDto
{
    public int TestId { get; set; }
    public Dictionary<int, List<int>> StudentAnswers { get; set; } = new();
}
