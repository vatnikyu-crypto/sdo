using System.Collections.Generic;

namespace SdoApp.Models.ViewModels
{
    public class UserDetailsViewModel
    {
        public User User { get; set; } = null!;
        public List<GroupWithCoursesViewModel> Groups { get; set; } = new();
    }

    public class GroupWithCoursesViewModel
    {
        public Group Group { get; set; } = null!;
        // Список курсов группы вместе с информацией, допущен ли туда студент
        public List<CourseAccessViewModel> Courses { get; set; } = new();
    }

    public class CourseAccessViewModel
    {
        public Course Course { get; set; } = null!;
        public int GroupConfigId { get; set; }
        public bool IsAllowed { get; set; } // Есть ли студент в AllowedStudentIds (или поле пустое)
    }
}