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
        // 1. Находим все обязательные материалы этого курса (Практики + Тесты, кроме Тренажеров)
        var mandatoryMaterials = await context.CourseMaterials
            .Where(m => m.CourseId == courseId && 
                       (m.Type == MaterialType.Assignment || 
                       (m.Type == MaterialType.Test && m.TestKind != TestStatus.Тренажер)))
            .ToListAsync();

        // Если в курсе нет оцениваемых элементов, зачет ставить не за что
        if (!mandatoryMaterials.Any()) return; 

        int totalMandatoryCount = mandatoryMaterials.Count;
        int completedMandatoryCount = 0;

        foreach (var material in mandatoryMaterials)
        {
            if (material.Type == MaterialType.Assignment)
            {
                // Задание зачтено, если админ его проверил и оценка выше двойки (3, 4, 5)
                var submission = await context.StudentSubmissions
                    .FirstOrDefaultAsync(s => s.CourseMaterialId == material.Id && s.StudentId == studentId && s.IsReviewed && s.Grade >= 3);
                
                if (submission != null)
                {
                    completedMandatoryCount++;
                }
            }
            else if (material.Type == MaterialType.Test)
            {
                // Тест сдан, если набранный % правильных ответов (Grade) >= порога теста (PassPercentage)
                var testResult = await context.StudentSubmissions
                    .FirstOrDefaultAsync(s => s.CourseMaterialId == material.Id && s.StudentId == studentId && s.Grade >= material.PassPercentage);

                if (testResult != null)
                {
                    completedMandatoryCount++;
                }
            }
        }

        // 2. Ищем существующую запись прогресса по курсу или создаем её
        var progress = await context.CourseProgresses
            .FirstOrDefaultAsync(p => p.StudentId == studentId && p.CourseId == courseId);

        if (progress == null)
        {
            progress = new CourseProgress { StudentId = studentId, CourseId = courseId };
            context.CourseProgresses.Add(progress);
        }

        // 3. Сверяем количество: если сданы ВСЕ обязательные элементы — ставим "Сдал" (IsCompleted = true)
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
