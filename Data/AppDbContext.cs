using Microsoft.EntityFrameworkCore;
using SdoApp.Models;
using System;

namespace SdoApp.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<CourseMaterial> CourseMaterials => Set<CourseMaterial>();
    public DbSet<StudentSubmission> StudentSubmissions => Set<StudentSubmission>();
    public DbSet<SubmissionMessage> SubmissionMessages => Set<SubmissionMessage>();
    public DbSet<TestQuestion> TestQuestions => Set<TestQuestion>();
    public DbSet<TestAnswerOption> TestAnswerOptions => Set<TestAnswerOption>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupStudent> GroupStudents => Set<GroupStudent>();
    public DbSet<GroupCourseConfig> GroupCourseConfigs => Set<GroupCourseConfig>();
    public DbSet<CourseProgress> CourseProgresses => Set<CourseProgress>();
    public DbSet<TestAttempt> TestAttempts => Set<TestAttempt>();
    public DbSet<GroupCompany> GroupCompanies => Set<GroupCompany>();
    public DbSet<KnowledgeBaseDocument> KnowledgeBaseDocuments => Set<KnowledgeBaseDocument>();
    public DbSet<SupportTicket> SupportTickets => Set<SupportTicket>();
    public DbSet<SupportMessage> SupportMessages => Set<SupportMessage>();
    public DbSet<TicketFile> TicketFiles => Set<TicketFile>();
    public DbSet<MintrudProgram> MintrudPrograms => Set<MintrudProgram>();
    public DbSet<CourseDocumentTemplate> CourseDocumentTemplates => Set<CourseDocumentTemplate>();
    public DbSet<CommissionMember> CommissionMembers => Set<CommissionMember>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // 1. Создаем тестовую компанию
        modelBuilder.Entity<Company>().HasData(
            new Company { Id = 1, Name = "ООО ТехноСфера", Inn = "7700000000" }
        );

        // 2. Добавляем 3 пользователей с разными ролями
        modelBuilder.Entity<User>().HasData(
            // ЗАПИСЬ 1: Главный Админ
            new User
            {
                Id = 1,
                Login = "superadmin",
                Password = "123", // Упростим пароли для удобства тестов
                LastName = "Иванов",
                FirstName = "Иван",
                MiddleName = "Иванович",
                BirthDate = new DateTime(1990, 5, 15),
                Snils = "123-456-789 01",
                Gender = Gender.Муж,
                Department = "Центральный офис",
                Role = UserRole.Главный_Админ,
                CompanyId = 1
            },
            // ЗАПИСЬ 2: Обычный Админ
            new User
            {
                Id = 2,
                Login = "admin",
                Password = "123",
                LastName = "Петров",
                FirstName = "Петр",
                MiddleName = "Петрович",
                BirthDate = new DateTime(1993, 8, 22),
                Snils = "987-654-321 00",
                Gender = Gender.Муж,
                Department = "Отдел кадров",
                Role = UserRole.Админ,
                CompanyId = 1
            },
            // ЗАПИСЬ 3: Слушатель (Обучающийся)
            new User
            {
                Id = 3,
                Login = "student",
                Password = "123",
                LastName = "Сидоров",
                FirstName = "Алексей",
                MiddleName = "Николаевич",
                Position = "Электромонтер 4 разряда",
                BirthDate = new DateTime(2001, 11, 3),
                Snils = "456-123-789 55",
                Gender = Gender.Муж,
                Department = "Цех сборки №2",
                Role = UserRole.Обучающийся,
                CompanyId = 1
            }
        );
        // 2. Добавляем экзаменационную комиссию
        modelBuilder.Entity<CommissionMember>().HasData(
            new CommissionMember
            {
                Id = 1,
                LastName = "Финк",
                FirstName = "Ольга",
                MiddleName = "Тимофеевна",
                Position = "Директор"
            },
            new CommissionMember
            {
                Id = 2,
                LastName = "Ткаченко",
                FirstName = "Тамара",
                MiddleName = "Владимировна",
                Position = "Заместитель директора"
            },
            new CommissionMember
            {
                Id = 3,
                LastName = "Бабий",
                FirstName = "Роман",
                MiddleName = "Николаевич",
                Position = "Старший мастер"
            },
            new CommissionMember
            {
                Id = 4,
                LastName = "Зубкова",
                FirstName = "Татьяна",
                MiddleName = "Павловна",
                Position = "Преподаватель"
            },
            new CommissionMember
            {
                Id = 5,
                LastName = "Фадеева",
                FirstName = "Оксана",
                MiddleName = "Валерьевна",
                Position = "Методист"
            },
            new CommissionMember
            {
                Id = 6,
                LastName = "Зуева",
                FirstName = "Анастасия",
                MiddleName = "Геннадьевна",
                Position = "Главный психолог"
            },
            new CommissionMember
            {
                Id = 7,
                LastName = "Бихтеев",
                FirstName = "Пётр",
                MiddleName = "Вячеславович",
                Position = "Специалист по ДО"
            }
        );
        // 3. ДОБАВЛЯЕМ ОФИЦИАЛЬНЫЕ ПРОГРАММЫ МИНТРУДА (Первые 4 сохранены, остальные отсортированы по коду)
        modelBuilder.Entity<MintrudProgram>().HasData(
            // --- НАЧАЛЬНЫЕ ЗНАЧЕНИЯ (БЕЗ ИЗМЕНЕНИЙ) ---
            new MintrudProgram { Id = 1, Code = "ИД46А", Name = "Общие вопросы охраны труда и функционирования системы управления охраной труда" },
            new MintrudProgram { Id = 2, Code = "ИД46Б", Name = "Безопасные методы и приемы выполнения работ при воздействии вредных и (или) опасных производственных факторов, источников опасности, идентифицированных в рамках специальной оценки условий труда и оценки профессиональных рисков" },
            new MintrudProgram { Id = 3, Code = "ИД39", Name = "Использование (применение) средств индивидуальной защиты" },
            new MintrudProgram { Id = 4, Code = "ИД34", Name = "Оказание первой помощи пострадавшим" },

            // --- ОТСОРТИРОВАННЫЙ ОСТАВШИЙСЯ СПИСОК ---
            new MintrudProgram { Id = 5, Code = "ИД46В_1", Name = "Безопасные методы и приемы выполнения земляных работ" },
            new MintrudProgram { Id = 6, Code = "ИД46В_2", Name = "Безопасные методы и приемы выполнения ремонтных, монтажных и демонтажных работ зданий и сооружений" },
            new MintrudProgram { Id = 7, Code = "ИД46В_3", Name = "Безопасные методы и приемы выполнения работ при размещении, монтаже, техническом обслуживании и ремонте технологического оборудования (включая технологическое оборудование)" },
            new MintrudProgram { Id = 8, Code = "ИД46В_4", Name = "Безопасные методы и приемы выполнения работ на высоте" },
            new MintrudProgram { Id = 9, Code = "ИД46В_5", Name = "Безопасные методы и приемы выполнения пожароопасных работ" },
            new MintrudProgram { Id = 10, Code = "ИД46В_6", Name = "Безопасные методы и приемы выполнения работ в ограниченных и замкнутых пространствах (ОЗП)" },
            new MintrudProgram { Id = 11, Code = "ИД46В_7", Name = "Безопасные методы и приемы выполнения строительных работ, в том числе: - окрасочные работы - электросварочные и газосварочные работы" },
            new MintrudProgram { Id = 12, Code = "ИД46В_8", Name = "Безопасные методы и приемы выполнения работ, связанных с опасностью воздействия сильнодействующих и ядовитых веществ" },
            new MintrudProgram { Id = 13, Code = "ИД46В_9", Name = "Безопасные методы и приемы выполнения газоопасных работ" },
            new MintrudProgram { Id = 14, Code = "ИД46В_10", Name = "Безопасные методы и приемы выполнения огневых работ" },
            new MintrudProgram { Id = 15, Code = "ИД46В_11", Name = "Безопасные методы и приемы выполнения работ, связанные с эксплуатацией подъемных сооружений" },
            new MintrudProgram { Id = 16, Code = "ИД46В_12", Name = "Безопасные методы и приемы выполнения работ, связанные с эксплуатацией тепловых энергоустановок" },
            new MintrudProgram { Id = 17, Code = "ИД46В_13", Name = "Безопасные методы и приемы выполнения работ в электроустановках" },
            new MintrudProgram { Id = 18, Code = "ИД46В_14", Name = "Безопасные методы и приемы выполнения работ, связанные с эксплуатацией сосудов, работающих под избыточным давлением" },
            new MintrudProgram { Id = 19, Code = "ИД46В_15", Name = "Безопасные методы и приемы обращения с животными" },
            new MintrudProgram { Id = 20, Code = "ИД46В_16", Name = "Безопасные методы и приемы при выполнении водолазных работ" },
            new MintrudProgram { Id = 21, Code = "ИД46В_17", Name = "Безопасные методы и приемы работ по поиску, идентификации, обезвреживанию и уничтожению взрывоопасных предметов" },
            new MintrudProgram { Id = 22, Code = "ИД46В_18", Name = "Безопасные методы и приемы работ в непосредственной близости от полотна или проезжей части эксплуатируемых автомобильных и железных дорог" },
            new MintrudProgram { Id = 23, Code = "ИД46В_19", Name = "Безопасные методы и приемы работ, на участках с патогенным заражением почвы" },
            new MintrudProgram { Id = 24, Code = "ИД46В_20", Name = "Безопасные методы и приемы работ по валке леса в особо опасных условиях" },
            new MintrudProgram { Id = 25, Code = "ИД46В_21", Name = "Безопасные методы и приемы работ по перемещению тяжеловесных и крупногабаритных грузов при отсутствии машин соответствующей грузоподъемности и разборке покосившихся и опасных (неправильно уложенных) штабелей круглых лесоматериалов" },
            new MintrudProgram { Id = 26, Code = "ИД46В_22", Name = "Безопасные методы и приемы работ с радиоактивными веществами и источниками ионизирующих излучений" },
            new MintrudProgram { Id = 27, Code = "ИД46В_23", Name = "Безопасные методы и приемы работ с ручным инструментом, в том числе с пиротехническим" },
            new MintrudProgram { Id = 28, Code = "ИД46В_24", Name = "Безопасные методы и приемы работ в театрах" }
        );
    }
}
