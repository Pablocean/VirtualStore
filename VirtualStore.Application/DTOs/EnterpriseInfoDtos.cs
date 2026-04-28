namespace VirtualStore.Application.DTOs;

public class EnterpriseInfoDto
{
    public string Id { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? LogoUrl { get; set; }
    public string? AboutUs { get; set; }
    public string? TermsAndConditions { get; set; }
    public string? PrivacyPolicy { get; set; }
}

public class UpdateEnterpriseInfoDto
{
    public string? CompanyName { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? LogoUrl { get; set; }
    public string? AboutUs { get; set; }
    public string? TermsAndConditions { get; set; }
    public string? PrivacyPolicy { get; set; }
}