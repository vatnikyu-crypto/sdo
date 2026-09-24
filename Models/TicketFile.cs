using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SdoApp.Models;

public class TicketFile
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int MessageId { get; set; }
    
    [ForeignKey("MessageId")]
    public SupportMessage? Message { get; set; }

    [Required]
    public string FilePath { get; set; } = string.Empty; // Путь к файлу в wwwroot/uploads/support/

    [Required]
    public string FileName { get; set; } = string.Empty; // Оригинальное имя файла для скачивания
}
