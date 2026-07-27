namespace DesktopTie.Startup;

public interface IStartupRegistrationManager
{
    StartupRegistrationResult GetStatus();

    StartupRegistrationResult Enable();

    StartupRegistrationResult Disable();
}
