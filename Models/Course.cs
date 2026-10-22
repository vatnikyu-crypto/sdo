using System;
using System.Collections.Generic;

namespace SdoApp.Models;

public class Course
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty; // Название курса
    public string? Description { get; set; } // Описание курса
    public string? ImageUrl { get; set; } // Путь к картинке-обложке
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
     public int? DurationHours { get; set; }        // Количество часов обучения
    public int? RecertificationMonths { get; set; } // Период переаттестации в месяцах (null = Бессрочно)

    // --- СВЯЗЬ С МИНТРУДОМ ---
    // Сделаем nullable (int?), чтобы старые курсы не сломались, 
    // и если вдруг будут курсы без привязки к Минтруду. Если привязка ОБЯЗАТЕЛЬНА, убери знак "?"
    public int? MintrudProgramId { get; set; } 
    public MintrudProgram? MintrudProgram { get; set; }

    // Связь: в одном курсе может быть много материалов (лекций, тестов)
    public List<CourseMaterial> Materials { get; set; } = new();
    public List<CourseDocumentTemplate> DocumentTemplates { get; set; } = new();
    public List<CourseTheme> Themes { get; set; } = new();
}
