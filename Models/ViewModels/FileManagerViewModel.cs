namespace SdoApp.Models.ViewModels;

public class FileItemDto
{
    public int Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; }
    public string ContextInfo { get; set; } = string.Empty; // Например: "Тикет №5 (Иванов)"
    public string SearchText { get; set; } = string.Empty; // Для поиска
}

public class FileManagerViewModel
{
    public List<FileItemDto> Items { get; set; } = new();
    public string ActiveTab { get; set; } = "support"; // support, submissions, group_courses
    public string SearchString { get; set; } = string.Empty;
    public int CurrentPage { get; set; } = 1;
    public int TotalPages { get; set; }
    public int TotalItems { get; set; }
    public int PageSize { get; set; } = 50;
}