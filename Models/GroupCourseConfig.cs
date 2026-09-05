using System;

namespace SdoApp.Models;

public class GroupCourseConfig
{
    public int Id { get; set; }
    
    public int GroupId { get; set; }
    public Group? Group { get; set; }

    public int CourseId { get; set; }
    public Course? Course { get; set; }

    // Документы и сроки по конкретному курсу в этой группе
    public string ProtocolNumber { get; set; } = string.Empty; // Номер протокола / приказа
    public DateTime? CourseStartDate { get; set; }              // Индивидуальная дата начала курса
    public DateTime? CourseEndDate { get; set; }                // Индивидуальная дата окончания курса

    // Трюк: Храним ID студентов, допущенных к этому курсу, в виде строки через запятую (например: "3,5,12,14")
    // Это позволит нам на лету убирать/ставить галочки конкретным людям без создания миллиарда таблиц!
    public string AllowedStudentIds { get; set; } = string.Empty;
}
