using System;
using System.ComponentModel.DataAnnotations;

namespace SdoApp.Models;

public class UserNotification
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int UserId { get; set; } // Кому предназначено (студенту)
    public User? User { get; set; }

    [Required]
    public string Message { get; set; } = string.Empty; // Текст уведомления

    public string? Link { get; set; } // Ссылка, куда перейти при клике (например, на страницу задания)

    public bool IsRead { get; set; } = false; // Прочитано или нет

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}