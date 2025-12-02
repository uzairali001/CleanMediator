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
}