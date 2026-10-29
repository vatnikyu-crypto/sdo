using Microsoft.AspNetCore.Http;

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
        public string? AttachmentUrl { get; set; } // <--- ДОБАВЛЕНО

        public string? StudentComment { get; set; }
        public IFormFile? SubmissionFile { get; set; }
    }
}