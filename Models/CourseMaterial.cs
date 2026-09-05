namespace SdoApp.Models;

public enum TestStatus
{
    Промежуточный,
    Итоговый,
    Тренажер
}

public class CourseMaterial
{
    public int Id { get; set; }
    public int CourseId { get; set; }
    public Course? Course { get; set; }
    public string Title { get; set; } = string.Empty;
    public MaterialType Type { get; set; }
    public string? ContentOrPath { get; set; }
    public int Order { get; set; }

    // --- НАСТРОЙКИ ДЛЯ БЛОКА ТЕСТИРОВАНИЯ ---
    public int TimeLimit { get; set; } = 0;
    public bool ShuffleQuestions { get; set; } = false;
    public int PassPercentage { get; set; } = 80;
    public TestStatus TestKind { get; set; } = TestStatus.Промежуточный; // Статус теста
    public int QuestionsCountToUse { get; set; } = 0; // 0 — использовать все вопросы
}
