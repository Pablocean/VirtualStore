using AutoMapper;
using Microsoft.Extensions.Logging.Abstractions;
using VirtualStore.Application.Mappings;
using Xunit;

namespace VirtualStore.UnitTests.Mappings;

public class MappingProfileTests
{
    [Fact]
    public void MappingProfile_Configuration_Is_Valid()
    {
        var config = new MapperConfiguration(cfg => cfg.AddProfile<MappingProfile>(), NullLoggerFactory.Instance);
        config.AssertConfigurationIsValid();
    }
}
