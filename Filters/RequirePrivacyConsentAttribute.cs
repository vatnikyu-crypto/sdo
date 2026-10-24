using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using SdoApp.Data;

namespace SdoApp.Filters;

public class RequirePrivacyConsentAttribute : ActionFilterAttribute
{
    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var httpContext = context.HttpContext;
        var user = httpContext.User;

        // 1. Только для авторизованных студентов
        if (user.Identity?.IsAuthenticated != true || !user.IsInRole("Обучающийся"))
        {
            await next();
            return;
        }

        // 2. Пропускаем служебные пути (согласие, авторизация, домашняя, статика)
        var path = httpContext.Request.Path.Value?.ToLower() ?? "";
        if (path.StartsWith("/consent") || 
            path.StartsWith("/auth") || 
            path.StartsWith("/home") || 
            path.StartsWith("/css") || 
            path.StartsWith("/js") || 
            path.StartsWith("/lib") || 
            path.StartsWith("/images") || 
            path.StartsWith("/uploads"))
        {
            await next();
            return;
        }

        // 3. AJAX-запросы не редиректим (для них вернёт 403)
        if (httpContext.Request.Headers["X-Requested-With"] == "XMLHttpRequest")
        {
            context.Result = new StatusCodeResult(403);
            return;
        }

        // 4. Получаем актуальные данные пользователя из БД
        var userIdClaim = user.FindFirst("UserId")?.Value;
        if (!int.TryParse(userIdClaim, out int userId))
        {
            await next();
            return;
        }

        var db = httpContext.RequestServices.GetRequiredService<AppDbContext>();
        var dbUser = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);

        // 5. Если не согласен — редирект на страницу согласия
        if (dbUser != null && !dbUser.IsPrivacyAccepted)
        {
            var returnUrl = httpContext.Request.Path + httpContext.Request.QueryString;
            context.Result = new RedirectResult($"/Consent?returnUrl={Uri.EscapeDataString(returnUrl)}");
            return;
        }

        await next();
    }
}