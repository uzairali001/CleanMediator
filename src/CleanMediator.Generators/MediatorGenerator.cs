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
        // We only need one pipeline now. We analyze decorators "on-demand" 
        // when we encounter the attribute on a Handler.
        var handlers = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (s, _) => IsClassWithInterfaces(s),
                transform: static (ctx, _) => GetHandlerInfo(ctx))
            .Where(static m => m is not null);

        var data = context.CompilationProvider.Combine(handlers.Collect());

        context.RegisterSourceOutput(data, static (spc, source) =>
            Execute(source.Left, source.Right, spc));
    }

    private static bool IsClassWithInterfaces(SyntaxNode node)
        => node is ClassDeclarationSyntax c && c.BaseList is { Types.Count: > 0 };

    // --- Handler Analysis ---

    private static HandlerInfo? GetHandlerInfo(GeneratorSyntaxContext context)
    {
        var classDeclaration = (ClassDeclarationSyntax)context.Node;
        var symbol = context.SemanticModel.GetDeclaredSymbol(classDeclaration);

        if (symbol is not INamedTypeSymbol typeSymbol || typeSymbol.IsAbstract || typeSymbol.TypeParameters.Length > 0)
            return null;

        foreach (var interfaceType in typeSymbol.AllInterfaces)
        {
            if (interfaceType.ContainingNamespace?.ToDisplayString() != "CleanMediator.Abstractions")
                continue;

            var name = interfaceType.Name;

            // Common logic to extract decorators from the Request Type
            if (name == "ICommandHandler" && interfaceType.TypeArguments.Length == 2)
            {
                var requestType = interfaceType.TypeArguments[0];
                return new HandlerInfo(
                    Type: HandlerType.Command,
                    ImplementationType: typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    InterfaceType: interfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    RequestType: requestType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    ResponseType: interfaceType.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    Decorators: GetDecoratorsFromAttribute(requestType)
                );
            }
            if (name == "ICommandHandler" && interfaceType.TypeArguments.Length == 1)
            {
                var requestType = interfaceType.TypeArguments[0];
                return new HandlerInfo(
                   Type: HandlerType.CommandVoid,
                   ImplementationType: typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                   InterfaceType: interfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                   RequestType: requestType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                   ResponseType: "global::System.Threading.Tasks.Task",
                   Decorators: GetDecoratorsFromAttribute(requestType)
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
                    Decorators: GetDecoratorsFromAttribute(requestType)
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
                    Decorators: ImmutableArray<DecoratorUsage>.Empty
                );
            }
        }
        return null;
    }

    private static ImmutableArray<DecoratorUsage> GetDecoratorsFromAttribute(ITypeSymbol requestType)
    {
        var builder = ImmutableArray.CreateBuilder<DecoratorUsage>();

        foreach (var attr in requestType.GetAttributes())
        {
            if (attr.AttributeClass?.Name != "DecoratorAttribute" &&
                attr.AttributeClass?.Name != "Decorator") continue;

            if (attr.AttributeClass?.ContainingNamespace?.ToDisplayString() != "CleanMediator.Abstractions")
                continue;

            if (attr.ConstructorArguments.Length > 0 &&
                attr.ConstructorArguments[0].Value is INamedTypeSymbol decoratorType)
            {
                // CRITICAL FIX: Handle Unbound Generics (typeof(X<,>))
                if (decoratorType.IsUnboundGenericType)
                    decoratorType = decoratorType.ConstructedFrom;
                else
                    decoratorType = decoratorType.OriginalDefinition;

                int order = 0;
                var orderArg = attr.NamedArguments.FirstOrDefault(kvp => kvp.Key == "Order");
                if (orderArg.Key != null && orderArg.Value.Value is int o)
                {
                    order = o;
                }
                else if (attr.ConstructorArguments.Length > 1 && attr.ConstructorArguments[1].Value is int positionalOrder)
                {
                    order = positionalOrder;
                }

                var deps = AnalyzeDecoratorConstructor(decoratorType);
                var typeParams = decoratorType.TypeParameters.Select(tp => tp.Name).ToImmutableArray();

                var fullType = decoratorType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var rawType = fullType.Split('<')[0];

                builder.Add(new DecoratorUsage(rawType, order, deps, typeParams));
            }
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<string> AnalyzeDecoratorConstructor(INamedTypeSymbol decoratorType)
    {
        // Double check we have the definition
        if (decoratorType.IsUnboundGenericType)
            decoratorType = decoratorType.ConstructedFrom;

        var ctor = decoratorType.Constructors.OrderByDescending(c => c.Parameters.Length).FirstOrDefault();

        // If still null, try OriginalDefinition as fallback
        if (ctor == null)
        {
            decoratorType = decoratorType.OriginalDefinition;
            ctor = decoratorType.Constructors.OrderByDescending(c => c.Parameters.Length).FirstOrDefault();
        }

        if (ctor == null) return ImmutableArray<string>.Empty;

        var dependencies = ImmutableArray.CreateBuilder<string>();

        foreach (var param in ctor.Parameters)
        {
            if (IsInnerHandlerParameter(param.Type))
            {
                dependencies.Add("INNER_HANDLER_PLACEHOLDER");
            }
            else
            {
                dependencies.Add(param.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            }
        }

        return dependencies.ToImmutable();
    }

    private static bool IsInnerHandlerParameter(ITypeSymbol type)
    {
        var typeString = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var defString = type.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        return typeString.Contains("CleanMediator.Abstractions.ICommandHandler") ||
               typeString.Contains("CleanMediator.Abstractions.IQueryHandler") ||
               defString.Contains("CleanMediator.Abstractions.ICommandHandler") ||
               defString.Contains("CleanMediator.Abstractions.IQueryHandler");
    }

    // --- Execution ---

    private static void Execute(Compilation compilation, ImmutableArray<HandlerInfo?> items, SourceProductionContext context)
    {
        var handlers = items.IsDefaultOrEmpty
            ? new List<HandlerInfo>()
            : items.Where(x => x is not null).Cast<HandlerInfo>().ToList();

        var sb = new StringBuilder();

        sb.AppendLine("""
using System;
using Microsoft.Extensions.DependencyInjection;

namespace CleanMediator.Generated
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddCleanMediator(this IServiceCollection services)
        {
            services.AddScoped<global::CleanMediator.Abstractions.IEventPublisher, GeneratedEventPublisher>();

""");

        foreach (var handler in handlers.Where(h => h.Type == HandlerType.Command || h.Type == HandlerType.Query || h.Type == HandlerType.CommandVoid).Distinct())
        {
            sb.AppendLine($"            // Handler: {handler.ImplementationType}");
            sb.AppendLine($"            services.AddScoped<{handler.ImplementationType}>();");
            sb.AppendLine($"            services.AddScoped<{handler.InterfaceType}>(sp => {{");
            sb.AppendLine($"                var handler = ({handler.InterfaceType})sp.GetRequiredService<{handler.ImplementationType}>();");

            var sortedDecorators = handler.Decorators.OrderBy(d => d.Order);

            foreach (var decorator in sortedDecorators)
            {
                // Safety check: Did we fail to find constructor parameters?
                if (decorator.ConstructorDependencies.IsEmpty)
                {
                    sb.AppendLine($"                // Warning: Could not find constructor for {decorator.TypeName}. Ensure it is public.");
                    // Fallback to empty constructor so code might compile if a default ctor exists, or fail visibly
                    sb.AppendLine($"                handler = new {decorator.TypeName}<{handler.RequestType}, {handler.ResponseType}>();");
                    continue;
                }

                var args = new List<string>();
                foreach (var dep in decorator.ConstructorDependencies)
                {
                    if (dep == "INNER_HANDLER_PLACEHOLDER")
                    {
                        args.Add("handler");
                    }
                    else
                    {
                        var resolvedDep = dep;

                        // Replace generic placeholders with concrete types
                        for (int i = 0; i < decorator.TypeParameters.Length; i++)
                        {
                            var paramName = decorator.TypeParameters[i];

                            if (paramName.Contains("Result") || paramName.Contains("Response"))
                            {
                                resolvedDep = resolvedDep.Replace(paramName, handler.ResponseType);
                            }
                            else if (paramName.Contains("Command") || paramName.Contains("Query") || paramName.Contains("Request"))
                            {
                                resolvedDep = resolvedDep.Replace(paramName, handler.RequestType);
                            }
                            else
                            {
                                if (i == 0) resolvedDep = resolvedDep.Replace(paramName, handler.RequestType);
                                if (i == 1) resolvedDep = resolvedDep.Replace(paramName, handler.ResponseType);
                            }
                        }

                        args.Add($"sp.GetRequiredService<{resolvedDep}>()");
                    }
                }

                sb.AppendLine($"                // Decorator: {decorator.TypeName} (Order: {decorator.Order})");
                sb.AppendLine($"                handler = new {decorator.TypeName}<{handler.RequestType}, {handler.ResponseType}>({string.Join(", ", args)});");
            }

            sb.AppendLine($"                return handler;");
            sb.AppendLine($"            }});");
            sb.AppendLine();
        }

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
    public class GeneratedEventPublisher : global::CleanMediator.Abstractions.IEventPublisher
    {
        private readonly IServiceProvider _serviceProvider;

        public GeneratedEventPublisher(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public async global::System.Threading.Tasks.Task PublishAsync<TEvent>(TEvent notification, global::System.Threading.CancellationToken ct)
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

    private class DecoratorUsage : IEquatable<DecoratorUsage>
    {
        public string TypeName { get; }
        public int Order { get; }
        public ImmutableArray<string> ConstructorDependencies { get; }
        public ImmutableArray<string> TypeParameters { get; }

        public DecoratorUsage(string typeName, int order, ImmutableArray<string> deps, ImmutableArray<string> typeParams)
        {
            TypeName = typeName;
            Order = order;
            ConstructorDependencies = deps;
            TypeParameters = typeParams;
        }

        public bool Equals(DecoratorUsage other) =>
            other != null &&
            TypeName == other.TypeName &&
            Order == other.Order &&
            ConstructorDependencies.SequenceEqual(other.ConstructorDependencies) &&
            TypeParameters.SequenceEqual(other.TypeParameters);

        public override bool Equals(object obj) => Equals(obj as DecoratorUsage);
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 23 + TypeName.GetHashCode();
                hash = hash * 23 + Order.GetHashCode();
                return hash;
            }
        }
    }

    private class HandlerInfo : IEquatable<HandlerInfo>
    {
        public HandlerType Type { get; }
        public string ImplementationType { get; }
        public string InterfaceType { get; }
        public string RequestType { get; }
        public string ResponseType { get; }
        public ImmutableArray<DecoratorUsage> Decorators { get; }

        public HandlerInfo(HandlerType Type, string ImplementationType, string InterfaceType, string RequestType, string ResponseType, ImmutableArray<DecoratorUsage> Decorators)
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
            Decorators.SequenceEqual(other.Decorators);

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