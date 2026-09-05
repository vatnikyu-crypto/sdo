using Microsoft.EntityFrameworkCore;
using SdoApp.Data;

var builder = WebApplication.CreateBuilder(args);

// Подключаем работу с контроллерами и HTML-представлениями (MVC)
builder.Services.AddControllersWithViews();

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

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication(); // Кто ты?
app.UseAuthorization();  // Что тебе разрешено?

// Настройка стандартного маршрута (открывает HomeController -> Index)
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Магия автоматического создания БД и добавления тестовой записи при старте
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.Run();
