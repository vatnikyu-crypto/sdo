using System.ComponentModel.DataAnnotations;

namespace SdoApp.Models;

public class CourseDocumentTemplate
{
    public int Id { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty; // Название шаблона (например: "Протокол", "Приказ об отчислении")

    public string? FilePath { get; set; } // Путь к физическому файлу шаблона на сервере (.docx или .xlsx)

    // ЧЕКБОКСЫ С ФОТО
    public bool IsPerStudent { get; set; } // Документ формируется отдельно на каждого слушателя
    public bool RequiresCommission { get; set; } // Требуется выбор комиссии

    // ПОРЯДОК СОРТИРОВКИ (ДЛЯ СТРЕЛОЧЕК И DRAG-AND-DROP)
    public int SortOrder { get; set; } = 0;

    // СВЯЗЬ С КУРСОМ: один курс может иметь много шаблонов документов
    public int CourseId { get; set; }
    public Course? Course { get; set; }
}
