using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Generic;

namespace CleanMediator.Generators;

[Generator]
public class EventPublisherGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // 1. Find all class declarations
        var classDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (s, _) => IsSyntaxTargetForGeneration(s),
                transform: static (ctx, _) => GetSemanticTargetForGeneration(ctx))
            .Where(static m => m is not null);

        // 2. Combine and Collect
        var compilationAndClasses = context.CompilationProvider.Combine(classDeclarations.Collect());

        // 3. Generate
        context.RegisterSourceOutput(compilationAndClasses, static (spc, source) => Execute(source.Left, source.Right, spc));
    }

    private static bool IsSyntaxTargetForGeneration(SyntaxNode node)
    {
        return node is ClassDeclarationSyntax c && c.BaseList is { Types.Count: > 0 };
    }

    private static HandlerInfo? GetSemanticTargetForGeneration(GeneratorSyntaxContext context)
    {
        var classDeclaration = (ClassDeclarationSyntax)context.Node;
        var symbol = context.SemanticModel.GetDeclaredSymbol(classDeclaration);

        if (symbol is not INamedTypeSymbol typeSymbol || typeSymbol.IsAbstract) return null;

        foreach (var interfaceType in typeSymbol.AllInterfaces)
        {
            // ROBUST MATCHING: Check Name, Namespace, and Generic Arity explicitly
            if (interfaceType.Name == "INotificationHandler" &&
                interfaceType.ContainingNamespace?.ToDisplayString() == "CleanMediator.Abstractions" &&
                interfaceType.IsGenericType)
            {
                var eventType = interfaceType.TypeArguments[0];
                return new HandlerInfo(
                    typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    eventType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                );
            }
        }

        return null;
    }

    private static void Execute(Compilation compilation, ImmutableArray<HandlerInfo?> handlers, SourceProductionContext context)
    {
        // Even if no handlers are found, we SHOULD generate the extension method 
        // to avoid "method not found" errors in Program.cs. It will just be empty.

        var validHandlers = handlers.Where(x => x is not null).Cast<HandlerInfo>().ToList();

        // Group handlers by the Event Type
        var eventMap = validHandlers
            .GroupBy(x => x.EventType)
            .ToDictionary(g => g.Key, g => g.Select(x => x.HandlerType).ToList());

        var sb = new StringBuilder();

        sb.AppendLine("""
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using CleanMediator.Abstractions;

namespace CleanMediator.Generated
{
    public class GeneratedEventPublisher : IEventPublisher
    {
        private readonly IServiceProvider _serviceProvider;

        public GeneratedEventPublisher(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public async Task PublishAsync<TEvent>(TEvent notification, CancellationToken ct)
        {
            var eventType = typeof(TEvent);
""");

        bool isFirst = true;
        foreach (var evt in eventMap)
        {
            var elseIf = isFirst ? "" : "else ";
            sb.AppendLine($$"""
            {{elseIf}}if (eventType == typeof({{evt.Key}}))
            {
                var concreteEvent = ({{evt.Key}})(object)notification!;
""");

            foreach (var handler in evt.Value)
            {
                sb.AppendLine($"""
                await _serviceProvider.GetRequiredService<{handler}>().HandleAsync(concreteEvent, ct);
""");
            }
            sb.AppendLine("            }");
            isFirst = false;
        }

        sb.AppendLine("""
        }
    }

    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddCleanMediator(this IServiceCollection services)
        {
            services.AddScoped<IEventPublisher, GeneratedEventPublisher>();
""");

        foreach (var handler in validHandlers.Select(h => h.HandlerType).Distinct())
        {
            sb.AppendLine($"            services.AddScoped<{handler}>();");
        }

        sb.AppendLine("""
            return services;
        }
    }
}
""");

        context.AddSource("GeneratedEventPublisher.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private class HandlerInfo : IEquatable<HandlerInfo>
    {
        public string HandlerType { get; }
        public string EventType { get; }

        public HandlerInfo(string handlerType, string eventType)
        {
            HandlerType = handlerType;
            EventType = eventType;
        }

        public bool Equals(HandlerInfo other) => other is not null && HandlerType == other.HandlerType && EventType == other.EventType;
        public override bool Equals(object obj) => Equals(obj as HandlerInfo);
        public override int GetHashCode()
        {
            unchecked { return (HandlerType.GetHashCode() * 397) ^ EventType.GetHashCode(); }
        }
    }
}