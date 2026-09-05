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

    // Связь: в одном курсе может быть много материалов (лекций, тестов)
    public List<CourseMaterial> Materials { get; set; } = new();
}
