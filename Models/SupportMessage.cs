using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SdoApp.Models;

public class SupportMessage
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int TicketId { get; set; }
    
    [ForeignKey("TicketId")]
    public SupportTicket? Ticket { get; set; }

    [Required]
    public int SenderId { get; set; } // ID отправителя (админ или студент)
    
    [ForeignKey("SenderId")]
    public User? Sender { get; set; }

    [Required]
    public string Text { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Прикрепленные файлы к КОНКРЕТНОМУ сообщению (до 3 штук)
    public ICollection<TicketFile> Files { get; set; } = new List<TicketFile>();
}
