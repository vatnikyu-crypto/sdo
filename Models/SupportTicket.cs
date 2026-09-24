using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SdoApp.Models;

public enum TicketStatus
{
    Открыто = 0,
    Закрыто = 1
}

public class SupportTicket
{
    [Key]
    public int Id { get; set; }

    [Required]
    [Phone]
    public string Phone { get; set; } = string.Empty; // Телефон для оперативной связи

    [Required]
    public TicketStatus Status { get; set; } = TicketStatus.Открыто;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Автор обращения
    [Required]
    public int StudentId { get; set; }
    
    [ForeignKey("StudentId")]
    public User? Student { get; set; }

    // Все сообщения внутри этого тикета (чат)
    public ICollection<SupportMessage> Messages { get; set; } = new List<SupportMessage>();
}
