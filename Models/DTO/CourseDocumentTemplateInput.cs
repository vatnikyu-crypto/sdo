// ВСПОМОГАТЕЛЬНЫЙ КЛАСС (DTO) ДЛЯ СВЯЗЫВАНИЯ ДАННЫХ ИЗ ФОРМЫ
// Размести его в самом низу файла контроллера вне класса CoursesController или в папке Models
public class CourseDocumentTemplateInput
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsPerStudent { get; set; }
    public bool RequiresCommission { get; set; }
    public string? ExistingFilePath { get; set; }
    public IFormFile? File { get; set; } // Для получения файлов .docx/.xlsx
}