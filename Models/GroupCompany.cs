using System;

namespace SdoApp.Models;

public class GroupCompany
{
    public int Id { get; set; }
    
    public int GroupId { get; set; }
    public Group? Group { get; set; }

    public int CompanyId { get; set; }
    public Company? Company { get; set; }
}
