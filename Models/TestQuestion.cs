using System.Collections.Generic;

namespace SdoApp.Models;

public class TestQuestion
{
    public int Id { get; set; }
    
    // Связь с конкретным элементом курса (ведь в таблице CourseMaterials тип Test — это и есть наш тест)
    public int CourseMaterialId { get; set; }
    public CourseMaterial? CourseMaterial { get; set; }

    public string QuestionText { get; set; } = string.Empty; // Текст вопроса (например: "Какое напряжение опасно для жизни?")

    public string? Hint { get; set; } // Подсказка к вопросу
    
    // Навигационное свойство: у одного вопроса может быть много вариантов ответа
    public List<TestAnswerOption> Answers { get; set; } = new();
}
