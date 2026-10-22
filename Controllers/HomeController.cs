using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SdoApp.Models;

namespace SdoApp.Controllers;

public class HomeController : Controller
{
    public IActionResult Index()
    {
        return View();
    }

    public IActionResult Privacy()
    {
        return View();
    }

    // ИСПРАВЛЕНО: Убрали [Route("NotFound")], чтобы работал путь /Home/NotFound
    public IActionResult newNotFound()
    {
        Response.StatusCode = 404; // Возвращаем правильный код для браузера
        return View("NotFound");   // Ищем Views/Shared/NotFound.cshtml
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}