using System;
using System.ComponentModel.DataAnnotations;

namespace SdoApp.Models;

public class CommissionMember
{
    [Key]
    public int Id { get; set; }

    [Required]
    public string LastName { get; set; } = string.Empty; // Фамилия

    [Required]
    public string FirstName { get; set; } = string.Empty; // Имя

    public string? MiddleName { get; set; } // Отчество

    [Required]
    public string Position { get; set; } = string.Empty; // Должность (например: Главный инженер по ОТ)

    // Удобное свойство для отображения в комбобоксах
    public string DisplayName => $"{LastName} {FirstName} {MiddleName} ({Position})".Trim();
}
