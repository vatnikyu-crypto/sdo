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
            .FirstOrDefaultAsync(m => m.Id == materialId);
        if (material == null) return false;

        var config = await context.GroupCourseConfigs
            .AsNoTracking()
            .Include(c => c.Group)
            .FirstOrDefaultAsync(c => c.GroupId == groupId && c.CourseId == material.CourseId);
        if (config?.Group == null) return false;

        // 1. Студент в группе?
        bool inGroup = await context.GroupStudents
            .AnyAsync(gs => gs.GroupId == groupId && gs.StudentId == studentId);
        if (!inGroup) return false;

        // 2. Даты (access-дата имеет приоритет)
        var today = DateTime.Today;
        var effectiveEnd = config.Group.MaterialAccessDate ?? config.Group.EndDate;
        if (today > effectiveEnd.Date) return false;
        if (today < config.Group.StartDate.Date) return false;

        // 3. AllowedStudentIds
        return IsStudentAllowed(config.AllowedStudentIds, studentId);
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
        if (today < config.Group.StartDate.Date) return false;

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
}