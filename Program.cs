using Microsoft.EntityFrameworkCore;
using SdoApp.Data;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Serilog;
using Serilog.Events;

// Настраиваем логирование
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.File(
        path: "Logs/log-.txt",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
    )
    .CreateLogger();

try
{


    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();

    // Подключаем работу с контроллерами и HTML-представлениями (MVC)
    builder.Services.AddControllersWithViews(options =>
    {
        options.Filters.Add<SdoApp.Filters.RequirePrivacyConsentAttribute>();
        options.Filters.Add(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute());
    });

    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<SdoApp.Services.AuditService>();

    // Настройка Rate Limiting
    builder.Services.AddRateLimiter(options =>
    {
        // Устанавливаем код ответа 429 вместо стандартного 503
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        // Настраиваем ответ при превышении лимита
        options.OnRejected = async (context, cancellationToken) =>
        {
            context.HttpContext.Response.ContentType = "application/json";
            await context.HttpContext.Response.WriteAsJsonAsync(new
            {
                success = false,
                message = "Слишком много попыток входа. Пожалуйста, подождите минуту и попробуйте снова."
            }, cancellationToken);
        };

        // Политика "login" — 5 попыток в минуту с одного IP
        options.AddPolicy("login", context =>
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0 // не ставим в очередь, сразу отклоняем
            });
        });
    });

    // СНИМАЕМ ЛИМИТЫ ЗАГРУЗКИ ФАЙЛОВ ДЛЯ ВСЕГО ПРИЛОЖЕНИЯ (Выставляем 500 МБ)
    builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
    {
        options.ValueLengthLimit = 524288000;
        options.MultipartBodyLengthLimit = 524288000; // Лимит на размер загружаемого файла через FormData
        options.MultipartHeadersLengthLimit = 524288000;
    });

    // Настройка самого сервера Kestrel (если проект запускается без IIS/Nginx напрямую)
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Limits.MaxRequestBodySize = 524288000; // Лимит на размер тела HTTP-запроса
    });

    // Настраиваем подключение к базе данных SQLite
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlite("Data Source=sdo.db"));

    // Подключаем аутентификацию через куки
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = "Cookies";
    })
    .AddCookie("Cookies", options =>
    {
        options.LoginPath = "/Auth/Login"; // Куда перенаправлять, если пользователь не вошел
        options.AccessDeniedPath = "/Auth/AccessDenied"; // Куда перенаправлять, если полез не в свою роль
    });

    var app = builder.Build();

    app.UseSerilogRequestLogging();

    app.Use(async (context, next) =>
    {
        context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
        await next();
    });

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Home/Error");
        app.UseHsts();
    }

    app.UseHttpsRedirection();
    app.UseStaticFiles();

    app.UseStatusCodePagesWithReExecute("/Home/NotFound");

    app.UseRouting();

    app.UseRateLimiter();

    app.UseAuthentication(); // Кто ты?
    app.UseAuthorization();  // Что тебе разрешено?

    // Настройка стандартного маршрута (открывает HomeController -> Index)
    app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Index}/{id?}");

    // Создание БД и очистка старых логов при старте
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // 1. Создаем БД, если её нет
        db.Database.EnsureCreated();

        // 2. Очищаем логи аудита старше 365 дней
        var oldLogs = db.AuditLogs.Where(l => l.Timestamp < DateTime.UtcNow.AddDays(-365));
        if (oldLogs.Any())
        {
            db.AuditLogs.RemoveRange(oldLogs);
            db.SaveChanges();
        }
    }

    app.MapGet("/", async context =>
    {
        context.Response.Redirect("/Auth/Login");
        await Task.CompletedTask;
    });

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Приложение упало при старте");
}
finally
{
    Log.CloseAndFlush();
}
