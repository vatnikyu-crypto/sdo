namespace SdoApp.Models;

public enum SubmissionStatus
{
    OnReview,   // На проверке (студент отправил, ждет админа)
    NeedFix,    // Нужна доработка (админ ответил, ждет исправлений студента)
    Completed   // Завершено (выставлена финальная оценка, чат закрыт)
}
