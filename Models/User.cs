using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SdoApp.Models;

public class User
{
    [Key]
    public int Id { get; set; }

    [Required]
    public string Login { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;

    [Required]
    public string LastName { get; set; } = string.Empty;

    [Required]
    public string FirstName { get; set; } = string.Empty;

    public string? MiddleName { get; set; } // Необязательно
    public DateTime? BirthDate { get; set; } // Необязательно
    public string? Snils { get; set; } // Необязательно

    [Required]
    public Gender Gender { get; set; }

    public string? Position { get; set; } = string.Empty; // Должность сотрудника (например: Сварщик, Монтажник)

    public string? Department { get; set; } = string.Empty;

    // === СОГЛАСИЕ НА ОБРАБОТКУ ПЕРСОНАЛЬНЫХ ДАННЫХ ===
    public bool IsPrivacyAccepted { get; set; } = false;
    public DateTime? PrivacyAcceptedAt { get; set; }

    [Required]
    public UserRole Role { get; set; }

    public int? CompanyId { get; set; }
    
    [ForeignKey("CompanyId")]
    public Company? Company { get; set; }
}
