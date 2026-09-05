using System;

namespace SdoApp.Models;

public class StudentSubmission
{
    public int Id { get; set; }
    public int CourseMaterialId { get; set; }
    public CourseMaterial? CourseMaterial { get; set; }

    public int StudentId { get; set; }
    public User? Student { get; set; }

    // Ответ студента (Текст + Файл)
    public string? StudentTextResponse { get; set; } 
    public string? StudentFilePath { get; set; } 
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;

    // Рецензия преподавателя (Мини-чат)
    public string? AdminReviewText { get; set; } 
    public int? Grade { get; set; } 
    public DateTime? ReviewedAt { get; set; }
    public bool IsReviewed { get; set; } = false; 
}
