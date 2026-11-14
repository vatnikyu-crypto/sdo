using System;
using System.Collections.Generic;

namespace SdoApp.Models;

public class SubmissionMessage
{
    public int Id { get; set; }
    
    public int StudentSubmissionId { get; set; }
    public StudentSubmission? StudentSubmission { get; set; }

    public int AuthorId { get; set; }
    public User? Author { get; set; }

    public string? TextContent { get; set; }
    
    // Старое поле (оставляем для совместимости с прошлыми сообщениями)
    public string? FilePath { get; set; }
    
    // === НОВОЕ: Список файлов к сообщению ===
    public List<SubmissionFile> Files { get; set; } = new();
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}