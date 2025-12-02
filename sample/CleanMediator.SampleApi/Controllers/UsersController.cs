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

    // New Endpoint for Query
    [HttpGet("{id}")]
    public async Task<IActionResult> Get(Guid id, [FromServices] IQueryHandler<GetUserQuery, UserDto?> _getUserHandler)
    {
        var query = new GetUserQuery(id);
        var result = await _getUserHandler.HandleAsync(query, CancellationToken.None);

        if (result is null) return NotFound();

        return Ok(result);
    }
}