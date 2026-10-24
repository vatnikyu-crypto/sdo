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
            score = finalScorePercentage,
            requiredScore = testMaterial.PassPercentage,
            isPassed = isPassed,
            redirectUrl = returnUrl,
            message = isPassed ? "🎉 Поздравляем! Тест успешно сдан." : "❌ К сожалению, вы не набрали проходной балл."
        });
    }
}