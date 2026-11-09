using SdoApp.Models;
using System.Collections.Generic;

namespace SdoApp.Models.ViewModels;

public class AdminDashboardViewModel
{
    // 1. Карточки KPI
    public int TotalStudents { get; set; }
    public int PendingAssignmentsCount { get; set; }
    public int OpenTicketsCount { get; set; }
    public int ActiveCoursesCount { get; set; }

    // 2. Таблица ожидающих проверки
    public List<StudentSubmission> PendingAssignments { get; set; } = new();

    // 3. Лента последних событий
    public List<AuditLog> RecentAuditLogs { get; set; } = new();

    // 4. Статистика тестов
    public int PassedTests { get; set; }
    public int FailedTests { get; set; }
}