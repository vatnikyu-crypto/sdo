using System;

namespace SdoApp.Models;

public class TestAttempt
{
    public int Id { get; set; }
    
    public int CourseMaterialId { get; set; }
    public CourseMaterial? CourseMaterial { get; set; }

    public int StudentId { get; set; }
    public User? Student { get; set; }

    public AttemptStatus Status { get; set; } = AttemptStatus.InProgress;
    
    // Процент правильных ответов
    public int ScorePercentage { get; set; } = 0;
    
    // Сколько вопросов верно из скольки предложенных
    public int CorrectAnswersCount { get; set; } = 0;
    public int TotalQuestionsCount { get; set; } = 0;

    // Тайминги сессии
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    
    // Системное поле: когда попытка ДОЛЖНА завершиться (StartedAt + TimeLimit)
    // Если TimeLimit == 0, то поле остается null (без лимита)
    public DateTime? HardDeadline { get; set; }
    public string? StudentAnswersJson { get; set; }
}
