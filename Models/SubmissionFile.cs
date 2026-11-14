using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SdoApp.Models;

public class SubmissionFile
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int MessageId { get; set; }
    
    [ForeignKey("MessageId")]
    public SubmissionMessage? Message { get; set; }

    [Required]
    public string FilePath { get; set; } = string.Empty; // /uploads/submissions/...

    [Required]
    public string FileName { get; set; } = string.Empty; // Оригинальное имя для скачивания
}