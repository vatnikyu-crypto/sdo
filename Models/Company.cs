using System.ComponentModel.DataAnnotations;

namespace SdoApp.Models;

public class Company
{
    [Key]
    public int Id { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    [StringLength(12, MinimumLength = 10)]
    public string Inn { get; set; } = string.Empty;

    // Связь: у одной компании может быть много пользователей
    public ICollection<User> Users { get; set; } = new List<User>();
}
