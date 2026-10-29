namespace SdoApp.Models;

public enum SubmissionStatus
{
    OnReview,   // Ждет проверки
    NeedFix,    // На доработке
    Completed   // Завершено
}