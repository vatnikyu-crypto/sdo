using System;
using System.Collections.Generic;

namespace SdoApp.Models;

public class Group
{
    public int Id { get; set; }
    public string GroupNumber { get; set; } = string.Empty; // Номер группы / Шифр приказа
    public string Title { get; set; } = string.Empty;       // Наименование группы
    public DateTime StartDate { get; set; }                 // Дата начала обучения (без времени)
    public DateTime EndDate { get; set; }                   // Дата окончания обучения (без времени)
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
