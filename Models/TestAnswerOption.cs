namespace SdoApp.Models;

public class TestAnswerOption
{
    public int Id { get; set; }
    
    public int TestQuestionId { get; set; } // К какому вопросу относится вариант
    public TestQuestion? TestQuestion { get; set; }

    public string AnswerText { get; set; } = string.Empty; // Текст ответа (например: "36 Вольт")
    public bool IsCorrect { get; set; } = false; // Это правильный ответ или нет?
}
