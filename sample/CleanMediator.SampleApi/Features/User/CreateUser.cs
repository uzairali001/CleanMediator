using CleanMediator.Abstractions;
using CleanMediator.Annotations;

namespace CleanMediator.SampleApi.Features.User;

//  The Command ---
public record CreateUserCommand(string Username, string Email) : IBaseCommand;

// The Command Handler ---
[Logging]
[Validation]
public class CreateUserHandler : ICommandHandler<CreateUserCommand, Guid>
{
    public async Task<Guid> HandleAsync(CreateUserCommand command, CancellationToken ct)
    {
        // Simulate Database Insertion
        await Task.Delay(100, ct);
        Console.WriteLine($"[Command] Creating user '{command.Username}' in Database...");

        return Guid.NewGuid();
    }
}

// The Event (Side Effects) ---
public record UserCreatedEvent(Guid UserId, string Email);

// Notification Handler A (Email) ---
public class SendWelcomeEmailHandler : INotificationHandler<UserCreatedEvent>
{
    public async Task HandleAsync(UserCreatedEvent notification, CancellationToken ct)
    {
        await Task.Delay(50, ct);
        Console.WriteLine($"[Event] ✉️ Sending welcome email to {notification.Email}...");
    }
}

// Notification Handler B (Audit) ---
public class AuditLogHandler : INotificationHandler<UserCreatedEvent>
{
    public async Task HandleAsync(UserCreatedEvent notification, CancellationToken ct)
    {
        await Task.Delay(50, ct);
        Console.WriteLine($"[Event] 🛡️ Auditing UserCreated for ID {notification.UserId}...");
    }
}