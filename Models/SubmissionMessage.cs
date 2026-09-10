using System;

namespace SdoApp.Models;

public class SubmissionMessage
{
    public int Id { get; set; }
    
    public int StudentSubmissionId { get; set; }
    public StudentSubmission? StudentSubmission { get; set; }

    public int AuthorId { get; set; } // ID автора (User.Id)
    public User? Author { get; set; }

    public string? TextContent { get; set; }
    public string? FilePath { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
