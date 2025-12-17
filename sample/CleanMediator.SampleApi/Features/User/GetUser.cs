using CleanMediator.Abstractions;
using CleanMediator.Annotations;

namespace CleanMediator.SampleApi.Features.User;

public record GetUserQuery(Guid UserId) : IBaseQuery;

// 2. The Result DTO
public record UserDto(Guid Id, string Username, string Email);

// 3. The Query Handler
//[Caching(durationInSeconds: 650)]
public class GetUserHandler : IQueryHandler<GetUserQuery, UserDto?>
{
    public async Task<UserDto?> HandleAsync(GetUserQuery query, CancellationToken ct)
    {
        // Simulate DB Fetch
        await Task.Delay(50, ct);

        if (query.UserId == Guid.Empty) return null;

        return new UserDto(query.UserId, "jdoe", "jdoe@example.com");
    }
}