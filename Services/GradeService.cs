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

        // 2. Вторым запросом вытаскиваем СРАЗУ ВСЕ ответы этого конкретного студента по этим материалам
        var studentSubmissions = await context.StudentSubmissions
            .Where(s => s.StudentId == studentId && mandatoryMaterialIds.Contains(s.CourseMaterialId))
            .ToListAsync();

        int totalMandatoryCount = mandatoryMaterials.Count;
        int completedMandatoryCount = 0;

        // 3. Считаем успешные сдачи в оперативной памяти сервера
        foreach (var material in mandatoryMaterials)
        {
            if (material.Type == MaterialType.Assignment)
            {
                // СИНХРОНИЗИРОВАНО: Задание зачтено, если тикет закрыт (Completed) и оценка выше двойки (3, 4, 5)
                bool isAssignmentPassed = studentSubmissions.Any(s => 
                    s.CourseMaterialId == material.Id && 
                    s.Status == SubmissionStatus.Completed && 
                    s.Grade >= 3);
                
                if (isAssignmentPassed) completedMandatoryCount++;
            }
            else if (material.Type == MaterialType.Test)
            {
                // СИНХРОНИЗИРОВАНО: Тест сдан, если статус Completed и процент правильных ответов выше порога
                bool isTestPassed = studentSubmissions.Any(s => 
                    s.CourseMaterialId == material.Id && 
                    s.Status == SubmissionStatus.Completed && 
                    s.Grade >= material.PassPercentage);

                if (isTestPassed) completedMandatoryCount++;
            }
        }

        // 4. Ищем существующую запись прогресса по курсу или создаем её
        var progress = await context.CourseProgresses
            .FirstOrDefaultAsync(p => p.StudentId == studentId && p.CourseId == courseId);

        if (progress == null)
        {
            progress = new CourseProgress { StudentId = studentId, CourseId = courseId };
            context.CourseProgresses.Add(progress);
        }

        // 5. Сверяем количество: если сданы ВСЕ обязательные элементы — ставим курс как завершенный
        if (completedMandatoryCount == totalMandatoryCount)
        {
            if (!progress.IsCompleted)
            {
                progress.IsCompleted = true;
                progress.CompletedAt = DateTime.UtcNow;
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
