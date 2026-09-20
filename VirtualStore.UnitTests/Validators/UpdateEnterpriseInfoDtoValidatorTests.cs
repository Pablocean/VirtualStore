using FluentAssertions;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Validators;
using Xunit;

namespace VirtualStore.UnitTests.Validators;

public class UpdateEnterpriseInfoDtoValidatorTests
{
    private readonly UpdateEnterpriseInfoDtoValidator _sut = new();

    [Fact]
    public void All_Null_Passes()
    {
        _sut.Validate(new UpdateEnterpriseInfoDto()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Valid_Full_Dto_Passes()
    {
        _sut.Validate(new UpdateEnterpriseInfoDto
        {
            CompanyName = "Acme",
            Address = "123 Main St",
            Phone = "555-1234",
            Email = "info@acme.com",
            LogoUrl = "https://acme.com/logo.png",
            AboutUs = "About",
            TermsAndConditions = "Terms",
            PrivacyPolicy = "Privacy"
        }).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Empty_CompanyName_Fails()
    {
        _sut.Validate(new UpdateEnterpriseInfoDto { CompanyName = "" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Long_CompanyName_Fails()
    {
        _sut.Validate(new UpdateEnterpriseInfoDto { CompanyName = new string('c', 201) }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Empty_Address_Fails()
    {
        _sut.Validate(new UpdateEnterpriseInfoDto { Address = "" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Long_Address_Fails()
    {
        _sut.Validate(new UpdateEnterpriseInfoDto { Address = new string('a', 501) }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Empty_Phone_Fails()
    {
        _sut.Validate(new UpdateEnterpriseInfoDto { Phone = "" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Long_Phone_Fails()
    {
        _sut.Validate(new UpdateEnterpriseInfoDto { Phone = new string('1', 21) }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Empty_Email_Fails()
    {
        _sut.Validate(new UpdateEnterpriseInfoDto { Email = "" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Malformed_Email_Fails()
    {
        _sut.Validate(new UpdateEnterpriseInfoDto { Email = "not-an-email" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Long_Email_Fails()
    {
        _sut.Validate(new UpdateEnterpriseInfoDto { Email = new string('a', 251) + "@t.com" }) // 257 chars > MaximumLength(256)
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void Invalid_LogoUrl_Fails()
    {
        _sut.Validate(new UpdateEnterpriseInfoDto { LogoUrl = "not-a-url" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void NonHttp_LogoUrl_Fails()
    {
        _sut.Validate(new UpdateEnterpriseInfoDto { LogoUrl = "ftp://example.com/logo.png" })
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void Long_LogoUrl_Fails()
    {
        _sut.Validate(new UpdateEnterpriseInfoDto { LogoUrl = "https://example.com/" + new string('a', 2048) })
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void Long_AboutUs_Fails()
    {
        _sut.Validate(new UpdateEnterpriseInfoDto { AboutUs = new string('a', 4001) }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Long_Terms_Fails()
    {
        _sut.Validate(new UpdateEnterpriseInfoDto { TermsAndConditions = new string('t', 8001) })
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void Long_Privacy_Fails()
    {
        _sut.Validate(new UpdateEnterpriseInfoDto { PrivacyPolicy = new string('p', 8001) })
            .IsValid.Should().BeFalse();
    }
}
