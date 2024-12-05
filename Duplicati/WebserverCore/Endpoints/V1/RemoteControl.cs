using Duplicati.Library.RestAPI;
using Duplicati.WebserverCore.Abstractions;
using Duplicati.WebserverCore.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace Duplicati.WebserverCore.Endpoints.V1;

public class RemoteControl : IEndpointV1
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/remotecontrol/status", ([FromServices] IRemoteControllerRegistration registration, [FromServices] IRemoteController remoteController)
            => GetStatus(registration, remoteController))
            .RequireAuthorization();

        // Don't allow these in agent-mode
        if (FIXMEGlobal.Origin == "Agent")
            return;

        group.MapPost("/remotecontrol/enable", ([FromServices] IRemoteControllerRegistration registration, [FromServices] IRemoteController remoteController)
            => EnableRemoteControl(registration, remoteController))
            .RequireAuthorization();

        group.MapPost("/remotecontrol/disable", ([FromServices] IRemoteControllerRegistration registration, [FromServices] IRemoteController remoteController)
            => DisableRemoteControl(registration, remoteController))
            .RequireAuthorization();

        group.MapDelete("/remotecontrol/registration", ([FromServices] IRemoteControllerRegistration registration, [FromServices] IRemoteController remoteController)
            => DeleteRegistration(registration, remoteController))
            .RequireAuthorization();

        group.MapPost("/remotecontrol/register", ([FromBody] Dto.StartRegistrationInput input, [FromServices] IRemoteControllerRegistration registration, [FromServices] IRemoteController remoteController, CancellationToken cancellationToken)
            => BeginRegisterMachine(registration, remoteController, input.RegistrationUrl, cancellationToken))
            .RequireAuthorization();

        group.MapDelete("/remotecontrol/register", ([FromServices] IRemoteControllerRegistration registration, [FromServices] IRemoteController remoteController)
            => CancelRegistration(registration, remoteController))
            .RequireAuthorization();
    }

    private static async Task<Dto.RemoteControlStatusOutput> BeginRegisterMachine(IRemoteControllerRegistration registration, IRemoteController remoteController, string registrationUrl, CancellationToken cancellationToken)
    {
        if (remoteController.CanEnable)
            throw new BadRequestException("Existing remote control must be removed before registering");

        if (!registration.IsRegistering && string.IsNullOrWhiteSpace(registrationUrl))
            throw new BadRequestException("Registration URL must be provided");

        var task = registration.IsRegistering
            ? registration.WaitForRegistration()
            : registration.RegisterMachine(registrationUrl);

        // Wait for registration, or at most 5 seconds
        // Client must poll if the registration is not completed by then
        await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(4.5), cancellationToken));

        // Rethrow any exceptions
        if (task.IsCompleted)
            await task;

        return GetStatus(registration, remoteController);
    }

    private static Dto.RemoteControlStatusOutput EnableRemoteControl(IRemoteControllerRegistration registration, IRemoteController remoteController)
    {
        remoteController.Enable();
        return GetStatus(registration, remoteController);
    }

    private static Dto.RemoteControlStatusOutput DisableRemoteControl(IRemoteControllerRegistration registration, IRemoteController remoteController)
    {
        remoteController.Disable();
        return GetStatus(registration, remoteController);
    }

    private static Dto.RemoteControlStatusOutput DeleteRegistration(IRemoteControllerRegistration registration, IRemoteController remoteController)
    {
        remoteController.DeleteRegistration();
        return GetStatus(registration, remoteController);
    }

    private static Dto.RemoteControlStatusOutput CancelRegistration(IRemoteControllerRegistration registration, IRemoteController remoteController)
    {
        registration.CancelRegisterMachine();
        return GetStatus(registration, remoteController);
    }

    private static Dto.RemoteControlStatusOutput GetStatus(IRemoteControllerRegistration registration, IRemoteController remoteController)
        => new Dto.RemoteControlStatusOutput(
            CanEnable: remoteController.CanEnable,
            IsEnabled: remoteController.IsEnabled,
            IsConnected: remoteController.Connected,
            IsRegistering: registration.IsRegistering,
            IsRegisteringFaulted: registration.IsRegistering && registration.WaitForRegistration().IsFaulted,
            IsRegisteringCompleted: registration.IsRegistering && registration.WaitForRegistration().IsCompleted,
            RegistrationUrl: registration.RegistrationUrl
        );
}