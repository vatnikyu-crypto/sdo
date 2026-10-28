using System.Collections.Generic;

namespace SdoApp.Models.DTO;

public class StudentQuestionDto
{
    public int Id { get; set; }
    public string QuestionText { get; set; } = string.Empty;
    public string? Hint { get; set; }
    public bool IsMultipleChoice { get; set; }
    public List<StudentAnswerOptionDto> Answers { get; set; } = new();
}

public class StudentAnswerOptionDto
{
    public int Id { get; set; }
    public string AnswerText { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
}
