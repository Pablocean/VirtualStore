namespace VirtualStore.Domain.Entities;

public class EnterpriseInfo : BaseEntity
{
    public string CompanyName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? LogoUrl { get; set; }
    public string? AboutUs { get; set; }
    public string? TermsAndConditions { get; set; }
    public string? PrivacyPolicy { get; set; }
}