using Microsoft.AspNetCore.Http;
using SdoApp.Models;
using System.Collections.Generic;

namespace SdoApp.Models.ViewModels
{
    public class AssignmentSubmitViewModel
    {
        public int MaterialId { get; set; }
        public int GroupId { get; set; }
        public int CourseId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string CourseTitle { get; set; } = string.Empty;

        public string Instruction { get; set; } = string.Empty;
        public string? AttachmentUrl { get; set; }

        public List<CourseMaterialFile> TaskFiles { get; set; } = new();

        // === НОВОЕ: Данные для чата ===
        public SubmissionStatus Status { get; set; } = SubmissionStatus.OnReview;
        public int? Grade { get; set; }
        public List<SubmissionMessage> Messages { get; set; } = new();

        // Оставляем на всякий случай, если будем использовать старую форму
        public string? StudentComment { get; set; }
        public IFormFile? SubmissionFile { get; set; }
    }
}