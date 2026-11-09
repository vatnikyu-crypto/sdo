using System;
using System.ComponentModel.DataAnnotations;

namespace SdoApp.Models;

public class AuditLog
{
    [Key]
    public int Id { get; set; }

    public int? UserId { get; set; } // Кто сделал (может быть null, если система)
    public User? User { get; set; }

    [Required]
    public string Action { get; set; } = string.Empty; // "Delete", "Update", "Login", "Export"

    public string? EntityType { get; set; } // "Course", "User", "Group"
    public int? EntityId { get; set; } // ID удаленного/измененного объекта

    [Required]
    public string Description { get; set; } = string.Empty; // Текст: "Удален курс 'Охрана труда'"

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}