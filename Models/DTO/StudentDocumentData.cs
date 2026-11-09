using SdoApp.Models;

namespace SdoApp.Models.DTO;

public class StudentDocumentData
{
    public User User { get; set; } = null!;
    public CourseProgress? Progress { get; set; }
}