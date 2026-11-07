using Microsoft.AspNetCore.Mvc;


namespace SdoApp.Helpers;

public static class ControllerExtensions
{
    /// <summary>
    /// Аналог Forbid() с учётом AJAX (повторяет логику RequirePrivacyConsentAttribute):
    /// - AJAX     → 403 + JSON (fetch не ломается на .json())
    /// - Обычный  → 302 на /Auth/AccessDenied (кастомная страница)
    /// </summary>
    public static IActionResult ForbidSmart(this Controller controller,
        string message = "Нет доступа.")
    {
        var request = controller.Request;

        bool isAjax = request.Headers["X-Requested-With"] == "XMLHttpRequest"
                   || request.Headers["Accept"].ToString().Contains("application/json");

        if (isAjax)
        {
            controller.Response.StatusCode = 403;
            return controller.Json(new
            {
                success = false,
                message,
                redirectUrl = "/Auth/AccessDenied"
            });
        }

        return controller.Redirect("/Auth/AccessDenied");
    }
}