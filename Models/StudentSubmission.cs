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

    public int GroupId { get; set; }
    public Group? Group { get; set; }

    public SubmissionStatus Status { get; set; } = SubmissionStatus.OnReview;
    public int? Grade { get; set; } // Оценка (2-5)
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; } = DateTime.UtcNow;

    // Связь с сообщениями чата
    public List<SubmissionMessage> Messages { get; set; } = new();
}