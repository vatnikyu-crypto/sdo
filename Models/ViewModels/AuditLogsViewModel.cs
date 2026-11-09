namespace SdoApp.Models.ViewModels;

public class AuditLogsViewModel
{
    public List<AuditLog> Logs { get; set; } = new();
    public string SearchString { get; set; } = string.Empty;
    public int CurrentPage { get; set; }
    public int TotalPages { get; set; }
    public int TotalItems { get; set; }
    public int PageSize { get; set; } = 50; // Показываем по 50 записей
}