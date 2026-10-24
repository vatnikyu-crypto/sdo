using System.Collections.Generic;

namespace SdoApp.Models.DTO;

public class TestSubmissionDto
{
    public int TestId { get; set; }
    public int CourseId { get; set; }
    public int GroupId { get; set; } // ДОБАВЛЕНО: Теперь обязательный
    public Dictionary<int, List<int>> StudentAnswers { get; set; } = new();
}