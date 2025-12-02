namespace CleanMediator.Generators.Tests;

public class EventPublisherGeneratorTests
{
    [Fact]
    public async Task Should_Generate_Publisher_For_Single_Handler()
    {
        // Arrange: Input code that uses our library
        var source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using CleanMediator.Abstractions;

        namespace TestNamespace;

        public class TestEvent { }

        public class TestHandler : INotificationHandler<TestEvent>
        {
            public Task HandleAsync(TestEvent notification, CancellationToken ct) => Task.CompletedTask;
        }
        """;

        // Act: Run the generator logic
        var (runResult, _) = await TestHelper.Verify(source);

        // Assert: Check the output
        var generatedSource = runResult.GeneratedTrees.SingleOrDefault();
        Assert.NotNull(generatedSource);

        var generatedCode = generatedSource!.ToString();

        // Check 1: Did it generate the extension method?
        Assert.Contains("public static IServiceCollection AddCleanMediator", generatedCode);

        // Check 2: Did it register the handler?
        // Note: Source Generators using FullyQualifiedFormat usually prepend 'global::'
        Assert.Contains("services.AddScoped<global::TestNamespace.TestHandler>();", generatedCode);

        // Check 3: Did it generate the dispatch logic?
        Assert.Contains("if (eventType == typeof(global::TestNamespace.TestEvent))", generatedCode);
        Assert.Contains("await _serviceProvider.GetRequiredService<global::TestNamespace.TestHandler>().HandleAsync(concreteEvent, ct);", generatedCode);
    }

    [Fact]
    public async Task Should_Generate_Publisher_For_Multiple_Handlers_Same_Event()
    {
        // Arrange
        var source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using CleanMediator.Abstractions;

        namespace TestNamespace;

        public class UserCreatedEvent { }

        // Handler 1
        public class EmailHandler : INotificationHandler<UserCreatedEvent>
        {
            public Task HandleAsync(UserCreatedEvent notification, CancellationToken ct) => Task.CompletedTask;
        }

        // Handler 2
        public class AuditHandler : INotificationHandler<UserCreatedEvent>
        {
            public Task HandleAsync(UserCreatedEvent notification, CancellationToken ct) => Task.CompletedTask;
        }
        """;

        // Act
        var (runResult, _) = await TestHelper.Verify(source);

        // Assert
        var generatedSource = runResult.GeneratedTrees.SingleOrDefault();
        Assert.NotNull(generatedSource);
        var generatedCode = generatedSource!.ToString();

        // Check 1: Both handlers registered in DI
        Assert.Contains("services.AddScoped<global::TestNamespace.EmailHandler>();", generatedCode);
        Assert.Contains("services.AddScoped<global::TestNamespace.AuditHandler>();", generatedCode);

        // Check 2: Both handlers called in the dispatch block
        Assert.Contains("await _serviceProvider.GetRequiredService<global::TestNamespace.EmailHandler>().HandleAsync(concreteEvent, ct);", generatedCode);
        Assert.Contains("await _serviceProvider.GetRequiredService<global::TestNamespace.AuditHandler>().HandleAsync(concreteEvent, ct);", generatedCode);
    }

    [Fact]
    public async Task Should_Ignore_Query_Handlers()
    {
        // Arrange
        // We define a Query Handler here. The generator should NOT see this as a Notification Handler.
        var source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using CleanMediator.Abstractions;

        namespace TestNamespace;

        public class TestQuery { }

        // Mocking the interface locally for the test compilation since we can't easily recompile the Abstractions assembly in this test harness context
        public interface IQueryHandler<in TQuery, TResult>
        {
            Task<TResult> HandleAsync(TQuery query, CancellationToken ct);
        }

        public class GetUserQueryHandler : IQueryHandler<TestQuery, string>
        {
            public Task<string> HandleAsync(TestQuery query, CancellationToken ct) => Task.FromResult("User");
        }
        """;

        // Act
        var (runResult, _) = await TestHelper.Verify(source);

        // Assert
        var generatedSource = runResult.GeneratedTrees.SingleOrDefault();

        // If no NotificationHandlers are found, it generates an empty extension method.
        // We want to ensure it DOES NOT generate registration or dispatch logic for GetUserQueryHandler.
        if (generatedSource != null)
        {
            var generatedCode = generatedSource.ToString();
            Assert.DoesNotContain("GetUserQueryHandler", generatedCode);
        }
    }
}