using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using SdoApp.Models;

namespace SdoApp.Services;

public static class AccessService
{
    /// <summary>Единая проверка: имеет ли студент доступ к материалу в группе.</summary>
    public static async Task<bool> HasAccessToMaterialAsync(
        AppDbContext context, int studentId, int materialId, int groupId)
    {
        var material = await context.CourseMaterials
            .AsNoTracking()
            .Select(m => new { m.Id, m.CourseId })
            .FirstOrDefaultAsync(m => m.Id == materialId);

        if (material == null) return false;

        // Вся проверка (членство, даты, AllowedStudentIds) — в одном месте
        return await HasAccessToCourseAsync(context, studentId, material.CourseId, groupId);
    }

    /// <summary>Проверка доступа к курсу в целом (для открытия страницы StudentView).</summary>
    public static async Task<bool> HasAccessToCourseAsync(
        AppDbContext context, int studentId, int courseId, int groupId)
    {
        var config = await context.GroupCourseConfigs
            .AsNoTracking()
            .Include(c => c.Group)
            .FirstOrDefaultAsync(c => c.GroupId == groupId && c.CourseId == courseId);
        if (config?.Group == null) return false;

        // 1. Студент в группе?
        bool inGroup = await context.GroupStudents
            .AnyAsync(gs => gs.GroupId == groupId && gs.StudentId == studentId);
        if (!inGroup) return false;

        // 2. Даты (MaterialAccessDate имеет приоритет над EndDate)
        var today = DateTime.Today;
        var effectiveEnd = config.Group.MaterialAccessDate ?? config.Group.EndDate;
        if (today > effectiveEnd.Date) return false;


        // 3. Индивидуальный допуск
        return IsStudentAllowed(config.AllowedStudentIds, studentId);
    }

    /// <summary>Семантика: null = все, "" = никто, "1,2,5" = явный список.</summary>
    public static bool IsStudentAllowed(string? allowedCsv, int studentId)
    {
        if (allowedCsv == null) return true;               // null = все
        if (string.IsNullOrWhiteSpace(allowedCsv)) return false; // "" = никто
        return allowedCsv.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Any(s => int.TryParse(s.Trim(), out var id) && id == studentId);
    }

    /// <summary>
    /// Возвращает список ID студентов группы, реально допущенных к курсу.
    /// null = все → вернёт всех студентов группы.
    /// "" = никто → вернёт пустой список.
    /// "1,2,5" → вернёт пересечение списка с реальными студентами группы.
    /// </summary>
    public static async Task<List<int>> GetAllowedStudentIdsAsync(
        AppDbContext context, int groupId, int courseId)
    {
        var allGroupStudentIds = await context.GroupStudents
            .AsNoTracking()
            .Where(gs => gs.GroupId == groupId)
            .Select(gs => gs.StudentId)
            .ToListAsync();

        var config = await context.GroupCourseConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.GroupId == groupId && c.CourseId == courseId);

        if (config == null) return new List<int>();

        if (config.AllowedStudentIds == null)
            return allGroupStudentIds;                     // null = все

        if (string.IsNullOrWhiteSpace(config.AllowedStudentIds))
            return new List<int>();                        // "" = никто

        var explicitIds = config.AllowedStudentIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s.Trim(), out var id) ? id : -1)
            .Where(id => id > 0)
            .ToHashSet();

        return allGroupStudentIds.Where(id => explicitIds.Contains(id)).ToList();
    }
}