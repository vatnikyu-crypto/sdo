using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using System;
using System.IO;
using System.Threading.Tasks;

namespace SdoApp.Controllers
{
    public class TestPageController : Controller
    {
        private readonly IWebHostEnvironment _env;

        public TestPageController(IWebHostEnvironment env)
        {
            _env = env;
        }

        // Обычный GET-запрос просто покажет форму
        [HttpGet]
        public IActionResult Index()
        {
            return View();
        }

        // POST-запрос примет данные и покажет текст успеха
        [HttpPost]
        public async Task<IActionResult> Index(int materialId, string studentComment, IFormFile submissionFile, int groupId)
        {
            try
            {
                string savedFilePath = "Файл не прикреплен";
                
                if (submissionFile != null && submissionFile.Length > 0)
                {
                    string uploadsFolder = Path.Combine(_env.WebRootPath, "uploads", "submissions");
                    if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);
                    
                    string uniqueFileName = Guid.NewGuid().ToString() + Path.GetExtension(submissionFile.FileName);
                    string fileSavePath = Path.Combine(uploadsFolder, uniqueFileName);
                    
                    using (var fileStream = new FileStream(fileSavePath, FileMode.Create))
                    {
                        await submissionFile.CopyToAsync(fileStream);
                    }
                    savedFilePath = $"/uploads/submissions/{uniqueFileName}";
                }

                // Просто выводим то, что пришло
                return Content($"✅ УСПЕХ!\n\nMaterialID: {materialId}\nGroupID: {groupId}\nКомментарий: {studentComment}\nПуть к файлу: {savedFilePath}");
            }
            catch (Exception ex)
            {
                return Content($"❌ ОШИБКА НА СЕРВЕРЕ: {ex.Message}");
            }
        }
    }
}