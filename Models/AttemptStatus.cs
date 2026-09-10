namespace SdoApp.Models;

public enum AttemptStatus
{
    InProgress,  // В процессе прохождения
    Passed,      // Успешно сдан (набран проходной балл)
    Failed,      // Не сдан (не набран проходной балл)
    Abandoned    // Брошен/Аннулирован (запущена новая попытка)
}
