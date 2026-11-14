using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SdoApp.Models;

public class GroupCourseConfigFile
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int GroupCourseConfigId { get; set; }
    
    [ForeignKey("GroupCourseConfigId")]
    public GroupCourseConfig? GroupCourseConfig { get; set; }

    [Required]
    public string FilePath { get; set; } = string.Empty; // /uploads/group_course_files/...

    [Required]
    public string FileName { get; set; } = string.Empty; // Оригинальное имя
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}