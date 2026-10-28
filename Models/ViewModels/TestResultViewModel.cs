using System;
using System.Collections.Generic;

namespace SdoApp.Models.ViewModels
{
    public class TestResultViewModel
    {
        public int AttemptId { get; set; }
        public int GroupId { get; set; }
        public string TestTitle { get; set; } = string.Empty;
        public string CourseTitle { get; set; } = string.Empty;
        public int CourseId { get; set; }
        public bool IsPassed { get; set; }
        public int ScorePercentage { get; set; }
        public int PassPercentage { get; set; }
        public int CorrectAnswers { get; set; }
        public int TotalQuestions { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        public TimeSpan Duration { get; set; }
        public List<QuestionResultItem> Questions { get; set; } = new();
    }

    public class QuestionResultItem
    {
        public int Number { get; set; }
        public string QuestionText { get; set; } = string.Empty;
        public List<string> StudentAnswers { get; set; } = new();
        public List<string> CorrectAnswers { get; set; } = new();
        public bool IsCorrect { get; set; }
    }
}