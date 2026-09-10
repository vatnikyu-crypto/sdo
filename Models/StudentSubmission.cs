using System;
using System.Collections.Generic;

namespace SdoApp.Models;

public class StudentSubmission
{
    public int Id { get; set; }
    
    public int CourseMaterialId { get; set; }
    public CourseMaterial? CourseMaterial { get; set; }

    public int StudentId { get; set; }
    public User? Student { get; set; }

    // Общие статусы тикета
    public SubmissionStatus Status { get; set; } = SubmissionStatus.OnReview;
    public int? Grade { get; set; } // null, пока статус OnReview или NeedFix
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; } = DateTime.UtcNow;

    // СВЯЗЬ: Внутри одной сдачи может быть целая цепочка сообщений (Чат)
    public List<SubmissionMessage> Messages { get; set; } = new();
}