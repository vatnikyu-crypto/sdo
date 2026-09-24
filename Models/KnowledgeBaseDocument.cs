using System;
using System.ComponentModel.DataAnnotations;

namespace SdoApp.Models;

public class KnowledgeBaseDocument
{
    [Key]
    public int Id { get; set; }

    [Required]
    [Display(Name = "Номер / Шифр документа")]
    public string DocumentNumber { get; set; } = string.Empty; // Например: № 197-ФЗ, ГОСТ Р 58144-2018

    [Required]
    [Display(Name = "Полное наименование")]
    public string Title { get; set; } = string.Empty; // Например: Трудовой кодекс Российской Федерации

    // Сделали необязательным. Если null — бэкенд автоматически запишет "Общие документы"
    [Display(Name = "Категория / Направление")]
    public string? Category { get; set; } 

    [Required]
    public string FilePath { get; set; } = string.Empty; // Путь к файлу в wwwroot/uploads/kb/

    public string FileExtension { get; set; } = string.Empty; // .pdf, .docx, .xlsx и т.д.

    public double FileSizeMb { get; set; } // Размер файла в Мегабайтах (для вывода пользователю)

    // Сделали необязательным (DateTime?). Если админ не указал — запишем текущую дату (считаем документ свежим)
    [Display(Name = "Дата редакции")]
    public DateTime? DocumentRevisionDate { get; set; } 

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow; // Дата загрузки файла в СДО
}
