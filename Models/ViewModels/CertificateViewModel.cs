using System;

namespace SdoApp.Models.ViewModels
{
    public class CertificateViewModel
    {
        public string MaskedFullName { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public string CompanyInn { get; set; } = string.Empty;
        public string ProgramName { get; set; } = string.Empty;
        public string? MintrudOfficialNumber { get; set; } 
        public string ProtocolNumber { get; set; } = string.Empty;
        public DateTime? ProtocolDate { get; set; }
        public DateTime? NextCheckDate { get; set; }
        public bool IsPassed { get; set; }
    }
}