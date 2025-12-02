using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

using System.Collections.Immutable;
using System.Text;

namespace CleanMediator.Generators;

[Generator]
public class MediatorGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var classDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (s, _) => IsClassWithInterfaces(s),
                transform: static (ctx, _) => GetSemanticInfo(ctx))
            .Where(static m => m is not null);

        var compilationAndClasses = context.CompilationProvider.Combine(classDeclarations.Collect());

        context.RegisterSourceOutput(compilationAndClasses, static (spc, source) => Execute(source.Left, source.Right, spc));
    }

    private static bool IsClassWithInterfaces(SyntaxNode node)
    {
        return node is ClassDeclarationSyntax c && c.BaseList is { Types.Count: > 0 };
    }

    private static HandlerInfo? GetSemanticInfo(GeneratorSyntaxContext context)
    {
        var classDeclaration = (ClassDeclarationSyntax)context.Node;
        var symbol = context.SemanticModel.GetDeclaredSymbol(classDeclaration);

        // FIX: Ignore Abstract classes AND Open Generics (like the Decorators themselves)
        // If we don't ignore generics, it tries to register Decorator<T> and fails because T is unknown.
        if (symbol is not INamedTypeSymbol typeSymbol || typeSymbol.IsAbstract || typeSymbol.TypeParameters.Length > 0)
            return null;

        foreach (var interfaceType in typeSymbol.AllInterfaces)
        {
            // ROBUST MATCHING: Check Name and Namespace specifically
            if (interfaceType.ContainingNamespace?.ToDisplayString() != "CleanMediator.Abstractions")
                continue;

            var name = interfaceType.Name; // e.g. "ICommandHandler"

            if (name == "ICommandHandler" && interfaceType.TypeArguments.Length == 2)
            {
                // ICommandHandler<TCommand, TResult>
                var requestType = interfaceType.TypeArguments[0];
                return new HandlerInfo(
                    Type: HandlerType.Command,
                    ImplementationType: typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    InterfaceType: interfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    RequestType: requestType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    ResponseType: interfaceType.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    Decorators: GetDecorators(requestType)
                );
            }

            if (name == "ICommandHandler" && interfaceType.TypeArguments.Length == 1)
            {
                // ICommandHandler<TCommand> (Void)
                var requestType = interfaceType.TypeArguments[0];
                return new HandlerInfo(
                   Type: HandlerType.CommandVoid,
                   ImplementationType: typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                   InterfaceType: interfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                   RequestType: requestType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                   ResponseType: "System.Threading.Tasks.Task", // Placeholder
                   Decorators: GetDecorators(requestType)
               );
            }

            if (name == "IQueryHandler" && interfaceType.TypeArguments.Length == 2)
            {
                var requestType = interfaceType.TypeArguments[0];
                return new HandlerInfo(
                    Type: HandlerType.Query,
                    ImplementationType: typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    InterfaceType: interfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    RequestType: requestType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    ResponseType: interfaceType.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    Decorators: GetDecorators(requestType)
                );
            }

            if (name == "INotificationHandler" && interfaceType.TypeArguments.Length == 1)
            {
                var eventType = interfaceType.TypeArguments[0];
                return new HandlerInfo(
                    Type: HandlerType.Event,
                    ImplementationType: typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    InterfaceType: interfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    RequestType: eventType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    ResponseType: "void",
                    Decorators: DecoratorFlags.None
                );
            }
        }

        return null;
    }

    private static DecoratorFlags GetDecorators(ITypeSymbol requestType)
    {
        var flags = DecoratorFlags.None;
        var attributes = requestType.GetAttributes();

        foreach (var attr in attributes)
        {
            var name = attr.AttributeClass?.Name;
            // Robust check: Matches "Logged" OR "LoggedAttribute"
            if (IsAttribute(name, "Logged")) flags |= DecoratorFlags.Logged;
            if (IsAttribute(name, "Validated")) flags |= DecoratorFlags.Validated;
            if (IsAttribute(name, "Cached")) flags |= DecoratorFlags.Cached;
        }
        return flags;
    }

    private static bool IsAttribute(string? actualName, string targetName)
    {
        if (string.IsNullOrEmpty(actualName)) return false;
        return actualName == targetName || actualName == targetName + "Attribute";
    }

    private static void Execute(Compilation compilation, ImmutableArray<HandlerInfo?> items, SourceProductionContext context)
    {
        // Even if empty, we generate the extension method to prevent compilation errors in Program.cs
        var handlers = items.IsDefaultOrEmpty
            ? new List<HandlerInfo>()
            : items.Where(x => x is not null).Cast<HandlerInfo>().ToList();

        var sb = new StringBuilder();

        sb.AppendLine("""
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CleanMediator.Abstractions;
using FluentValidation;
using Microsoft.Extensions.Caching.Memory;

// IMPORTANT: We assume behaviors are in this namespace. 
// If you move them, change this line or move them to a shared namespace.
using CleanMediator.SampleApi.Behaviors; 

namespace CleanMediator.Generated
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddCleanMediator(this IServiceCollection services)
        {
            // Register Publisher
            services.AddScoped<IEventPublisher, GeneratedEventPublisher>();

""");

        // 1. Register Commands and Queries (with Decorators)
        foreach (var handler in handlers.Where(h => h.Type == HandlerType.Command || h.Type == HandlerType.Query || h.Type == HandlerType.CommandVoid).Distinct())
        {
            // A. Register the Concrete Handler
            sb.AppendLine($"            services.AddScoped<{handler.ImplementationType}>();");

            // B. Register the Interface with Factory
            sb.AppendLine($"            services.AddScoped<{handler.InterfaceType}>(sp => {{");
            sb.AppendLine($"                var handler = ({handler.InterfaceType})sp.GetRequiredService<{handler.ImplementationType}>();");

            // Validated
            if (handler.Decorators.HasFlag(DecoratorFlags.Validated))
            {
                sb.AppendLine($"                var validators = sp.GetRequiredService<System.Collections.Generic.IEnumerable<IValidator<{handler.RequestType}>>>();");
                sb.AppendLine($"                handler = new ValidationDecorator<{handler.RequestType}, {handler.ResponseType}>(handler, validators);");
            }

            // Logged
            if (handler.Decorators.HasFlag(DecoratorFlags.Logged))
            {
                sb.AppendLine($"                var logger = sp.GetRequiredService<ILogger<LoggingDecorator<{handler.RequestType}, {handler.ResponseType}>>>();");
                sb.AppendLine($"                handler = new LoggingDecorator<{handler.RequestType}, {handler.ResponseType}>(handler, logger);");
            }

            // Cached (Queries only)
            if (handler.Decorators.HasFlag(DecoratorFlags.Cached) && handler.Type == HandlerType.Query)
            {
                sb.AppendLine($"                var cache = sp.GetRequiredService<IMemoryCache>();");
                sb.AppendLine($"                var cacheLogger = sp.GetRequiredService<ILogger<CachingDecorator<{handler.RequestType}, {handler.ResponseType}>>>();");
                sb.AppendLine($"                handler = new CachingDecorator<{handler.RequestType}, {handler.ResponseType}>(handler, cache, cacheLogger);");
            }

            sb.AppendLine($"                return handler;");
            sb.AppendLine($"            }});");
            sb.AppendLine();
        }

        // 2. Register Notification Handlers
        var notificationHandlers = handlers.Where(h => h.Type == HandlerType.Event).ToList();
        foreach (var handler in notificationHandlers.Select(h => h.ImplementationType).Distinct())
        {
            sb.AppendLine($"            services.AddScoped<{handler}>();");
        }

        sb.AppendLine("""
            return services;
        }
    }
""");

        GenerateEventPublisher(sb, notificationHandlers);

        sb.AppendLine("}");

        context.AddSource("CleanMediator.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static void GenerateEventPublisher(StringBuilder sb, List<HandlerInfo> handlers)
    {
        sb.AppendLine("""
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

        var eventGroups = handlers.GroupBy(x => x.RequestType);
        bool isFirst = true;

        foreach (var group in eventGroups)
        {
            var elseIf = isFirst ? "" : "else ";
            sb.AppendLine($$"""
            {{elseIf}}if (eventType == typeof({{group.Key}}))
            {
                var concreteEvent = ({{group.Key}})(object)notification!;
""");
            foreach (var h in group)
            {
                sb.AppendLine($"                await _serviceProvider.GetRequiredService<{h.ImplementationType}>().HandleAsync(concreteEvent, ct);");
            }
            sb.AppendLine("            }");
            isFirst = false;
        }

        sb.AppendLine("""
        }
    }
""");
    }

    private enum HandlerType { Command, CommandVoid, Query, Event }

    [System.Flags]
    private enum DecoratorFlags { None = 0, Logged = 1, Validated = 2, Cached = 4 }

    private class HandlerInfo : IEquatable<HandlerInfo>
    {
        public HandlerType Type { get; }
        public string ImplementationType { get; }
        public string InterfaceType { get; }
        public string RequestType { get; }
        public string ResponseType { get; }
        public DecoratorFlags Decorators { get; }

        public HandlerInfo(HandlerType Type, string ImplementationType, string InterfaceType, string RequestType, string ResponseType, DecoratorFlags Decorators)
        {
            this.Type = Type;
            this.ImplementationType = ImplementationType;
            this.InterfaceType = InterfaceType;
            this.RequestType = RequestType;
            this.ResponseType = ResponseType;
            this.Decorators = Decorators;
        }

        public bool Equals(HandlerInfo other) =>
            other != null &&
            Type == other.Type &&
            ImplementationType == other.ImplementationType &&
            InterfaceType == other.InterfaceType &&
            Decorators == other.Decorators;

        public override bool Equals(object obj) => Equals(obj as HandlerInfo);
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 23 + Type.GetHashCode();
                hash = hash * 23 + ImplementationType.GetHashCode();
                return hash;
            }
        }
    }
}