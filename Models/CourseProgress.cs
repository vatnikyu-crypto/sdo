using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SdoApp.Models;

public class CourseProgress
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int StudentId { get; set; }
    [ForeignKey("StudentId")]
    public User? Student { get; set; }

    [Required]
    public int CourseId { get; set; }
    [ForeignKey("CourseId")]
    public Course? Course { get; set; }

    // Итоговый статус по курсу: true — Сдал, false — В процессе обучения
    public bool IsCompleted { get; set; } = false;

    public DateTime? CompletedAt { get; set; }
}
