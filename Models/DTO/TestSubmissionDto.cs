using System.Collections.Generic;

namespace SdoApp.Models.DTO;

public class TestSubmissionDto
{
    public int TestId { get; set; }
    public int CourseId { get; set; } // Добавили поле, чтобы фронтенд мог передать ID курса
    public Dictionary<int, List<int>> StudentAnswers { get; set; } = new();
}
