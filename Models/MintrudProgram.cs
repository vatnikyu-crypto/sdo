using System.Collections.Generic;

namespace SdoApp.Models;

public class MintrudProgram
{
    public int Id { get; set; }
    
    // Код/шифр программы (например, "15.04.05" или "Профстандарт №12"). 
    // Если кодов в списке не будет, можно оставить пустую строку или сделать nullable
    public string Code { get; set; } = string.Empty; 
    
    public string NumberProgram {get; set;} = string.Empty;

    // Полное официальное название программы Минтруда
    public string Name { get; set; } = string.Empty;

    // Навигационное свойство: один элемент справочника Минтруда может использоваться в разных курсах
    public List<Course> Courses { get; set; } = new();
}
