namespace Juvin.App.Tests;

public sealed class ServiceRegistrationsTests
{
    [Fact]
    public void Build_ValidatesEveryRegistrationOnThisOs()
    {
        using var services = ServiceRegistrations.Build();

        Assert.NotNull(services);
    }
}
