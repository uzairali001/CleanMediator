using CleanMediator.Abstractions;
using CleanMediator.SampleApi.Features.User;

using Microsoft.AspNetCore.Mvc;

namespace CleanMediator.SampleApi.Controllers;

[ApiController]
[Route("api/users")]
public class UsersController(
    IEventPublisher publisher
) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserCommand command, [FromServices] ICommandHandler<CreateUserCommand, Guid> createUserHandler)
    {
        // A. Execute the Primary Logic
        var userId = await createUserHandler.HandleAsync(command, CancellationToken.None);

        // B. Publish Side-Effects
        // The Source Generator created the routing logic for this!
        var evt = new UserCreatedEvent(userId, command.Email);
        await publisher.PublishAsync(evt, CancellationToken.None);

        return Ok(new { UserId = userId });
    }
}