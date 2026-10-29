using System;

namespace SdoApp.Models;

public class SubmissionMessage
{
    public int Id { get; set; }
    
    public int StudentSubmissionId { get; set; }
    public StudentSubmission? StudentSubmission { get; set; }

    public int AuthorId { get; set; } // Кто отправил (Студент или Админ)
    public User? Author { get; set; }

    public string? TextContent { get; set; } // Текст
    public string? FilePath { get; set; }    // Путь к файлу (если есть)
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}