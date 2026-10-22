using System.ComponentModel.DataAnnotations;

namespace SdoApp.Models;

public class CourseTheme
{
    public int Id { get; set; }
    
    public int CourseId { get; set; }
    public Course? Course { get; set; }
    
    [Required]
    public string Title { get; set; } = string.Empty;
    
    public int Order { get; set; } // Для сортировки тем
    
    // Связь: у одной темы много материалов
    public ICollection<CourseMaterial> Materials { get; set; } = new List<CourseMaterial>();
}