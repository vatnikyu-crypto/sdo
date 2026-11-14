using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SdoApp.Models;

public class CourseMaterialFile
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int CourseMaterialId { get; set; }
    
    [ForeignKey("CourseMaterialId")]
    public CourseMaterial? CourseMaterial { get; set; }

    [Required]
    public string FilePath { get; set; } = string.Empty; // /uploads/materials/assignments/...

    [Required]
    public string FileName { get; set; } = string.Empty; // Оригинальное имя
}