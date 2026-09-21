using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Moq;
using VirtualStore.API.OpenApi;
using Xunit;

namespace VirtualStore.UnitTests.OpenApi;

/// <summary>
/// Wave T1c remainder suite for <see cref="BearerSecuritySchemeTransformer"/>.
/// Drives <c>TransformAsync</c> with an <see cref="OpenApiDocument"/> and covers
/// the Bearer-scheme present vs absent arms. No I/O: the scheme provider is mocked.
/// </summary>
public class BearerSecuritySchemeTransformerTests
{
    private sealed class StubHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(AuthenticateResult.NoResult());
    }

    private static Mock<IAuthenticationSchemeProvider> SchemeProvider(params AuthenticationScheme[] schemes)
    {
        var provider = new Mock<IAuthenticationSchemeProvider>();
        provider.Setup(p => p.GetAllSchemesAsync())
            .ReturnsAsync((IEnumerable<AuthenticationScheme>)schemes);
        return provider;
    }

    private static OpenApiDocumentTransformerContext EmptyContext()
    {
        // The transformer never reads the context; bypass the internal ctor so
        // this suite does not couple to its (framework-owned) shape.
        return (OpenApiDocumentTransformerContext)RuntimeHelpers.GetUninitializedObject(
            typeof(OpenApiDocumentTransformerContext));
    }

    [Fact]
    public async Task TransformAsync_NoBearerScheme_LeavesDocumentUntouched()
    {
        var provider = SchemeProvider(
            new AuthenticationScheme("Cookies", "Cookies", typeof(StubHandler)));
        var transformer = new BearerSecuritySchemeTransformer(provider.Object);
        var document = new OpenApiDocument();

        await transformer.TransformAsync(document, EmptyContext(), CancellationToken.None);

        document.Components.Should().BeNull();
        (document.Security is null || document.Security.Count == 0).Should().BeTrue();
    }

    [Fact]
    public async Task TransformAsync_NoSchemesAtAll_LeavesDocumentUntouched()
    {
        var transformer = new BearerSecuritySchemeTransformer(SchemeProvider().Object);
        var document = new OpenApiDocument();

        await transformer.TransformAsync(document, EmptyContext(), CancellationToken.None);

        document.Components.Should().BeNull();
        (document.Security is null || document.Security.Count == 0).Should().BeTrue();
    }

    [Fact]
    public async Task TransformAsync_BearerSchemePresent_RegistersSecuritySchemeAndRequirement()
    {
        var provider = SchemeProvider(
            new AuthenticationScheme("Bearer", "Bearer", typeof(StubHandler)));
        var transformer = new BearerSecuritySchemeTransformer(provider.Object);
        var document = new OpenApiDocument();

        await transformer.TransformAsync(document, EmptyContext(), CancellationToken.None);

        document.Components.Should().NotBeNull();
        document.Components!.SecuritySchemes.Should().ContainKey("Bearer");
        var scheme = document.Components!.SecuritySchemes!["Bearer"].Should()
            .BeOfType<OpenApiSecurityScheme>().Subject;
        scheme.Type.Should().Be(SecuritySchemeType.Http);
        scheme.Scheme.Should().Be("bearer");
        scheme.In.Should().Be(ParameterLocation.Header);
        scheme.BearerFormat.Should().Be("JWT");

        document.Security.Should().ContainSingle();
        var requirement = document.Security![0];
        requirement.Should().ContainSingle();
        requirement.Single().Key.Reference?.Id.Should().Be("Bearer");
    }

    [Fact]
    public async Task TransformAsync_BearerAmongOthers_StillRegistersOnce()
    {
        var provider = SchemeProvider(
            new AuthenticationScheme("Cookies", "Cookies", typeof(StubHandler)),
            new AuthenticationScheme("Bearer", "Bearer", typeof(StubHandler)));
        var transformer = new BearerSecuritySchemeTransformer(provider.Object);
        var document = new OpenApiDocument();

        await transformer.TransformAsync(document, EmptyContext(), CancellationToken.None);

        document.Components!.SecuritySchemes.Should().ContainSingle(kv => kv.Key == "Bearer");
        document.Security.Should().ContainSingle();
    }
}
