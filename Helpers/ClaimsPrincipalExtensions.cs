using System.Security.Claims;

namespace SdoApp.Helpers;

public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Централизованно достает ID пользователя из Claims.
    /// Ищет по всем возможным ключам, которые могут встретиться в старом коде.
    /// </summary>
    public static int GetUserId(this ClaimsPrincipal user)
    {
        if (user == null) return 0;

        // Перебираем все возможные варианты, которые были в проекте
        var userIdStr = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? user.FindFirst("UserId")?.Value
                     ?? user.FindFirst("Id")?.Value;

        if (string.IsNullOrEmpty(userIdStr))
        {
            // На всякий случай проверяем Identity.Name (если туда писали ID)
            userIdStr = user.Identity?.Name;
        }

        return int.TryParse(userIdStr, out int userId) ? userId : 0;
    }
}