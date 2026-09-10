using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace SdoApp.Services;

public class GradeService
{
    public static async Task RecalculateCourseProgressAsync(AppDbContext context, int studentId, int courseId)
    {
        // 1. Одним запросом забираем все обязательные материалы этого курса (кроме тренажеров)
        var mandatoryMaterials = await context.CourseMaterials
            .Where(m => m.CourseId == courseId && 
                       (m.Type == MaterialType.Assignment || 
                       (m.Type == MaterialType.Test && m.TestKind != TestStatus.Тренажер)))
            .ToListAsync();

        // Если в курсе нет оцениваемых элементов, зачет ставить не за что
        if (!mandatoryMaterials.Any()) return; 

        var mandatoryMaterialIds = mandatoryMaterials.Select(m => m.Id).ToList();

        // 2. Запрашиваем из базы все проверенные практические задания студента по этому курсу
        var studentAssignments = await context.StudentSubmissions
            .Where(s => s.StudentId == studentId 
                     && mandatoryMaterialIds.Contains(s.CourseMaterialId) 
                     && s.Status == SubmissionStatus.Completed)
            .ToListAsync();

        // 3. ИСПРАВЛЕНО: Запрашиваем из новой таблицы TestAttempts ВСЕ успешные попытки тестов студента
        var studentPassedTests = await context.TestAttempts
            .Where(a => a.StudentId == studentId 
                     && mandatoryMaterialIds.Contains(a.CourseMaterialId) 
                     && a.Status == AttemptStatus.Passed)
            .ToListAsync();

        int totalMandatoryCount = mandatoryMaterials.Count;
        int completedMandatoryCount = 0;

        // 4. Сверяем каждый обязательный материал в памяти сервера
        foreach (var material in mandatoryMaterials)
        {
            if (material.Type == MaterialType.Assignment)
            {
                // Задание сдано, если есть закрытый тикет с оценкой 3, 4 или 5
                bool isAssignmentPassed = studentAssignments.Any(s => 
                    s.CourseMaterialId == material.Id && s.Grade >= 3);
                
                if (isAssignmentPassed) completedMandatoryCount++;
            }
            else if (material.Type == MaterialType.Test)
            {
                // ИСПРАВЛЕНО: Тест считается сданным, если в таблице TestAttempts 
                // есть хотя бы одна запись со статусом Passed по этому материалу
                bool isTestPassed = studentPassedTests.Any(a => a.CourseMaterialId == material.Id);

                if (isTestPassed) completedMandatoryCount++;
            }
        }

        // 5. Ищем существующую запись прогресса по курсу или создаем её
        var progress = await context.CourseProgresses
            .FirstOrDefaultAsync(p => p.StudentId == studentId && p.CourseId == courseId);

        if (progress == null)
        {
            progress = new CourseProgress { StudentId = studentId, CourseId = courseId };
            context.CourseProgresses.Add(progress);
        }

        // 6. Если количество успешно сданных элементов совпало с общим числом обязательных — закрываем курс!
        if (completedMandatoryCount == totalMandatoryCount)
        {
            if (!progress.IsCompleted)
            {
                progress.IsCompleted = true;
                progress.CompletedAt = DateTime.UtcNow; // Фиксируем точную дату окончания курса
            }
        }
        else
        {
            progress.IsCompleted = false;
            progress.CompletedAt = null;
        }

        await context.SaveChangesAsync();
    }
}
