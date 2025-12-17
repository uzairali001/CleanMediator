using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
        // 1. Pipeline: Find Decorators (e.g. CachingDecorator) marked with [GenerateDecorator]
        var decoratorClasses = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (s, _) => IsClassWithAttribute(s),
                transform: static (ctx, _) => GetDecoratorDef(ctx))
            .Where(static m => m is not null);

        // 2. Output: Generate the Attributes (e.g. CachedAttribute.g.cs)
        context.RegisterSourceOutput(decoratorClasses, static (spc, decorator) =>
            GenerateAttributeSource(spc, decorator));

        // 3. Pipeline: Find Handlers (Commands/Queries)
        var handlers = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (s, _) => IsClassWithInterfaces(s),
                transform: static (ctx, _) => GetHandlerInfo(ctx))
            .Where(static m => m is not null);

        // 4. Combine data for Wiring
        var compilationData = context.CompilationProvider
            .Combine(handlers.Collect())
            .Combine(decoratorClasses.Collect());

        // 5. Output: Generate the DI Wiring (CleanMediator.g.cs)
        context.RegisterSourceOutput(compilationData, static (spc, source) =>
            ExecuteDI(source.Left.Right, source.Right, spc));
    }

    // --- Predicates ---

    private static bool IsClassWithAttribute(SyntaxNode node)
        => node is ClassDeclarationSyntax c && c.AttributeLists.Count > 0;

    private static bool IsClassWithInterfaces(SyntaxNode node)
        => node is ClassDeclarationSyntax c && c.BaseList is { Types.Count: > 0 };

    // --- Analysis: Decorator Definitions ---

    private static DecoratorDef? GetDecoratorDef(GeneratorSyntaxContext context)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;
        var symbol = context.SemanticModel.GetDeclaredSymbol(classDecl);
        if (symbol is not INamedTypeSymbol typeSymbol) return null;

        // Find [GenerateDecorator("Name")]
        string? attrName = null;
        foreach (var attr in typeSymbol.GetAttributes())
        {
            if (attr.AttributeClass?.Name == "GenerateDecoratorAttribute")
            {
                attrName = attr.ConstructorArguments.FirstOrDefault().Value as string;
                break;
            }
        }

        if (attrName == null) return null;

        // Analyze Constructor to separate Config (Attribute params) vs Services (DI params)
        var ctor = typeSymbol.Constructors.OrderByDescending(c => c.Parameters.Length).FirstOrDefault();
        if (ctor == null) return null;

        var configParams = new List<ConfigParam>();
        var serviceParams = new List<ServiceParam>();

        foreach (var p in ctor.Parameters)
        {
            if (IsInnerHandlerParameter(p.Type))
            {
                serviceParams.Add(new ServiceParam(p.Name, "INNER_HANDLER"));
            }
            else if (IsConfigType(p.Type))
            {
                // UNWRAP NULLABLE: Attributes cannot accept int?, only int.
                ITypeSymbol targetType = p.Type;
                if (p.Type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
                    p.Type is INamedTypeSymbol named && named.TypeArguments.Length > 0)
                {
                    targetType = named.TypeArguments[0];
                }

                string? defaultValue = null;
                // Only set default value if it exists AND is not null (int x = null is invalid)
                if (p.HasExplicitDefaultValue && p.ExplicitDefaultValue != null)
                {
                    defaultValue = FormatValue(p.ExplicitDefaultValue, targetType);
                }

                configParams.Add(new ConfigParam(p.Name, targetType.ToDisplayString(), defaultValue));
            }
            else
            {
                serviceParams.Add(new ServiceParam(p.Name, p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
            }
        }

        var fullTypeName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Split('<')[0];
        var typeParams = typeSymbol.TypeParameters.Select(t => t.Name).ToImmutableArray();

        return new DecoratorDef(attrName, fullTypeName, configParams.ToImmutableArray(), serviceParams.ToImmutableArray(), typeParams.ToImmutableArray());
    }

    private static bool IsConfigType(ITypeSymbol type)
    {
        // Handle Nullable<T>
        if (type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            if (type is INamedTypeSymbol named && named.TypeArguments.Length > 0)
            {
                return IsConfigType(named.TypeArguments[0]);
            }
        }

        // Expanded list of allowed Attribute parameter types
        return type.SpecialType == SpecialType.System_String ||
               type.SpecialType == SpecialType.System_Boolean ||
               type.SpecialType == SpecialType.System_Byte ||
               type.SpecialType == SpecialType.System_SByte ||
               type.SpecialType == SpecialType.System_Int16 ||
               type.SpecialType == SpecialType.System_UInt16 ||
               type.SpecialType == SpecialType.System_Int32 ||
               type.SpecialType == SpecialType.System_UInt32 ||
               type.SpecialType == SpecialType.System_Int64 ||
               type.SpecialType == SpecialType.System_UInt64 ||
               type.SpecialType == SpecialType.System_Single ||
               type.SpecialType == SpecialType.System_Double ||
               type.SpecialType == SpecialType.System_Char ||
               type.TypeKind == TypeKind.Enum;
    }

    private static string FormatValue(object value, ITypeSymbol type)
    {
        if (value == null) return "null";

        if (type.TypeKind == TypeKind.Enum)
        {
            // Cast integer value back to Enum type string: (EnumType)1
            return $"({type.ToDisplayString()}){value}";
        }

        if (value is bool b) return b ? "true" : "false";
        if (value is string s) return $"\"{s}\"";
        if (value is char c) return $"'{c}'";
        if (value is float f) return $"{f}f";
        if (value is double d) return $"{d}d";
        return value.ToString();
    }

    // --- Generation: Attributes ---

    private static void GenerateAttributeSource(SourceProductionContext spc, DecoratorDef? def)
    {
        if (def == null) return;

        var className = def.AttributeName.EndsWith("Attribute") ? def.AttributeName : $"{def.AttributeName}Attribute";

        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine();
        sb.AppendLine("namespace CleanMediator.Annotations");
        sb.AppendLine("{");
        sb.AppendLine($"    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]");
        sb.AppendLine($"    public class {className} : Attribute");
        sb.AppendLine("    {");

        // Properties
        foreach (var p in def.ConfigParams)
        {
            sb.AppendLine($"        public {p.Type} {ToPascalCase(p.Name)} {{ get; }}");
        }
        sb.AppendLine($"        public int Order {{ get; set; }} = int.MaxValue;");

        // Constructor
        var ctorArgsList = def.ConfigParams.Select(p =>
        {
            var arg = $"{p.Type} {p.Name}";
            if (p.DefaultValue != null) arg += $" = {p.DefaultValue}";
            return arg;
        });

        var ctorArgs = string.Join(", ", ctorArgsList);
        sb.AppendLine();
        sb.AppendLine($"        public {className}({ctorArgs})");
        sb.AppendLine("        {");
        foreach (var p in def.ConfigParams)
        {
            sb.AppendLine($"            {ToPascalCase(p.Name)} = {p.Name};");
        }
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        spc.AddSource($"{className}.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    // --- Analysis: Handlers ---

    private static HandlerInfo? GetHandlerInfo(GeneratorSyntaxContext context)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;
        var symbol = context.SemanticModel.GetDeclaredSymbol(classDecl);
        if (symbol is not INamedTypeSymbol typeSymbol || typeSymbol.IsAbstract || typeSymbol.TypeParameters.Length > 0) return null;

        foreach (var i in typeSymbol.AllInterfaces)
        {
            if (i.ContainingNamespace?.ToDisplayString() != "CleanMediator.Abstractions") continue;
            if (i.Name == "ICommandHandler" || i.Name == "IQueryHandler")
            {
                var request = i.TypeArguments[0];
                var response = i.TypeArguments.Length > 1
                    ? i.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    : "global::System.Threading.Tasks.Task";

                // CHANGED: Use Syntax-based extraction to handle 'Order' on generated attributes correctly
                var attributes = GetDecoratorUsagesFromSyntax(classDecl);

                return new HandlerInfo(
                    typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    i.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    request.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    response,
                    attributes
                );
            }
            if (i.Name == "INotificationHandler")
            {
                var eventType = i.TypeArguments.Length > 0
                   ? i.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                   : "";

                return new HandlerInfo(
                    typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    "EVENT",
                    eventType,
                    "void",
                    ImmutableArray<DecoratorUsage>.Empty
                );
            }
        }
        return null;
    }

    // NEW METHOD: Syntactic extraction of attributes to bypass Semantic Model limitations on Generated Types
    private static ImmutableArray<DecoratorUsage> GetDecoratorUsagesFromSyntax(ClassDeclarationSyntax classDecl)
    {
        var builder = ImmutableArray.CreateBuilder<DecoratorUsage>();

        foreach (var attrList in classDecl.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var name = attr.Name.ToString();

                // Normalize Name (remove alias, namespace, suffix)
                var lastDot = name.LastIndexOf('.');
                if (lastDot >= 0) name = name.Substring(lastDot + 1);
                if (name.EndsWith("Attribute")) name = name.Substring(0, name.Length - "Attribute".Length);

                int order = int.MaxValue;
                var args = ImmutableArray.CreateBuilder<string>();

                if (attr.ArgumentList != null)
                {
                    foreach (var arg in attr.ArgumentList.Arguments)
                    {
                        if (arg.NameEquals != null)
                        {
                            // Named Argument (Order = 1)
                            if (arg.NameEquals.Name.Identifier.Text == "Order")
                            {
                                var expr = arg.Expression.ToString();
                                if (int.TryParse(expr, out int o))
                                {
                                    order = o;
                                }
                            }
                        }
                        else
                        {
                            // Positional Argument (Config Values)
                            // We capture the raw expression string (e.g., "30", "\"hello\"")
                            args.Add(arg.Expression.ToString());
                        }
                    }
                }

                builder.Add(new DecoratorUsage(name, order, args.ToImmutable()));
            }
        }
        return builder.ToImmutable();
    }

    // --- Generation: DI Wiring ---

    private static void ExecuteDI(ImmutableArray<HandlerInfo?> handlers, ImmutableArray<DecoratorDef?> decorators, SourceProductionContext context)
    {
        var validHandlers = handlers.Where(x => x != null).Cast<HandlerInfo>().ToList();
        var decoratorMap = decorators.Where(x => x != null).Cast<DecoratorDef>().ToDictionary(d => d.AttributeName);

        var sb = new StringBuilder();
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("namespace CleanMediator.Generated");
        sb.AppendLine("{");
        sb.AppendLine("    public static class ServiceCollectionExtensions");
        sb.AppendLine("    {");
        sb.AppendLine("        public static IServiceCollection AddCleanMediator(this IServiceCollection services)");
        sb.AppendLine("        {");
        sb.AppendLine("            services.AddScoped<global::CleanMediator.Abstractions.IEventPublisher, GeneratedEventPublisher>();");

        foreach (var h in validHandlers.Where(x => x.InterfaceType != "EVENT").Distinct())
        {
            sb.AppendLine($"            // Handler: {h.ImplType}");
            sb.AppendLine($"            services.AddScoped<{h.ImplType}>();");
            sb.AppendLine($"            services.AddScoped<{h.InterfaceType}>(sp => {{");
            sb.AppendLine($"                var handler = ({h.InterfaceType})sp.GetRequiredService<{h.ImplType}>();");

            var sortedDecorators = h.Decorators.OrderBy(d => d.Order);

            // Reverse is needed because we wrap Inner -> Outer.
            // If Order 1 (Log) and Order 2 (Cache), we want Log(Cache(Handler)).
            // Construction: new Log(new Cache(Handler)).
            // So we iterate: Cache -> Log (Inner -> Outer).
            // Wait, Sort ascending: 1, 2.
            // Loop: 
            // 1. handler = new Cache(handler)  (Order 1)
            // 2. handler = new Log(handler)    (Order 2)
            // Final structure: Log(Cache(Inner))
            // Execution: Log -> Cache -> Inner
            // This assumes Order 1 = Inner-most. 
            // Usually Order 1 = First to execute (Outer-most).
            // If Order 1 = Outer-most, we need to construct it LAST.
            // So we need to reverse the sorted list.

            foreach (var usage in sortedDecorators.Reverse())
            {
                if (decoratorMap.TryGetValue(usage.Name, out var def))
                {
                    var ctorArgs = new List<string>();
                    int configIndex = 0;

                    foreach (var p in def.ServiceParams)
                    {
                        if (p.Type == "INNER_HANDLER") ctorArgs.Add("handler");
                        else
                        {
                            var resolved = p.Type;
                            for (int i = 0; i < def.TypeParams.Length; i++)
                            {
                                if (i==0) resolved = resolved.Replace(def.TypeParams[i], h.RequestType);
                                if (i==1) resolved = resolved.Replace(def.TypeParams[i], h.ResponseType);
                            }
                            ctorArgs.Add($"sp.GetRequiredService<{resolved}>()");
                        }
                    }
                    foreach (var p in def.ConfigParams)
                    {
                        if (configIndex < usage.Args.Length)
                            ctorArgs.Add(usage.Args[configIndex++]);
                        else if (p.DefaultValue != null)
                            ctorArgs.Add(p.DefaultValue);
                        else
                            ctorArgs.Add("default");
                    }

                    sb.AppendLine($"                handler = new {def.FullTypeName}<{h.RequestType}, {h.ResponseType}>({string.Join(", ", ctorArgs)});");
                }
            }
            sb.AppendLine($"                return handler;");
            sb.AppendLine($"            }});");
            sb.AppendLine();
        }

        var notificationHandlers = validHandlers.Where(h => h.InterfaceType == "EVENT").ToList();
        foreach (var handler in notificationHandlers.Select(h => h.ImplType).Distinct())
        {
            sb.AppendLine($"            services.AddScoped<{handler}>();");
        }

        sb.AppendLine("            return services;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");

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
                sb.AppendLine($"                await _serviceProvider.GetRequiredService<{h.ImplType}>().HandleAsync(concreteEvent, ct);");
            }
            sb.AppendLine("            }");
            isFirst = false;
        }
        sb.AppendLine("""
        }
    }
""");
    }

    private static string ToPascalCase(string s) => char.ToUpper(s[0]) + s.Substring(1);
    private static string FormatLiteral(TypedConstant c) => c.ToCSharpString();
    private static bool IsInnerHandlerParameter(ITypeSymbol t) => t.ToDisplayString().Contains("ICommandHandler") || t.ToDisplayString().Contains("IQueryHandler");

    // Helper Classes
    private class DecoratorDef : IEquatable<DecoratorDef>
    {
        public string AttributeName { get; }
        public string FullTypeName { get; }
        public ImmutableArray<ConfigParam> ConfigParams { get; }
        public ImmutableArray<ServiceParam> ServiceParams { get; }
        public ImmutableArray<string> TypeParams { get; }

        public DecoratorDef(string attributeName, string fullTypeName, ImmutableArray<ConfigParam> configParams, ImmutableArray<ServiceParam> serviceParams, ImmutableArray<string> typeParams)
        {
            AttributeName = attributeName;
            FullTypeName = fullTypeName;
            ConfigParams = configParams;
            ServiceParams = serviceParams;
            TypeParams = typeParams;
        }

        public bool Equals(DecoratorDef other)
        {
            if (other is null) return false;
            return AttributeName == other.AttributeName &&
                   FullTypeName == other.FullTypeName &&
                   ConfigParams.SequenceEqual(other.ConfigParams) &&
                   ServiceParams.SequenceEqual(other.ServiceParams) &&
                   TypeParams.SequenceEqual(other.TypeParams);
        }

        public override bool Equals(object obj) => Equals(obj as DecoratorDef);
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                hash = hash * 23 + AttributeName.GetHashCode();
                hash = hash * 23 + FullTypeName.GetHashCode();
                return hash;
            }
        }
    }

    private class ConfigParam : IEquatable<ConfigParam>
    {
        public string Name { get; }
        public string Type { get; }
        public string DefaultValue { get; }

        public ConfigParam(string name, string type, string defaultValue)
        {
            Name = name;
            Type = type;
            DefaultValue = defaultValue;
        }

        public bool Equals(ConfigParam other) => other != null && Name == other.Name && Type == other.Type && DefaultValue == other.DefaultValue;
        public override bool Equals(object obj) => Equals(obj as ConfigParam);
        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 23 + Name.GetHashCode();
                h = h * 23 + Type.GetHashCode();
                h = h * 23 + (DefaultValue?.GetHashCode() ?? 0);
                return h;
            }
        }
    }

    private class ServiceParam : IEquatable<ServiceParam>
    {
        public string Name { get; }
        public string Type { get; }
        public ServiceParam(string name, string type) { Name = name; Type = type; }
        public bool Equals(ServiceParam other) => other != null && Name == other.Name && Type == other.Type;
        public override bool Equals(object obj) => Equals(obj as ServiceParam);
        public override int GetHashCode() => Name.GetHashCode() ^ Type.GetHashCode();
    }

    private class DecoratorUsage : IEquatable<DecoratorUsage>
    {
        public string Name { get; }
        public int Order { get; }
        public ImmutableArray<string> Args { get; }
        public DecoratorUsage(string name, int order, ImmutableArray<string> args) { Name = name; Order = order; Args = args; }
        public bool Equals(DecoratorUsage other) => other != null && Name == other.Name && Order == other.Order && Args.SequenceEqual(other.Args);
        public override bool Equals(object obj) => Equals(obj as DecoratorUsage);
        public override int GetHashCode() => Name.GetHashCode() ^ Order.GetHashCode();
    }

    private class HandlerInfo : IEquatable<HandlerInfo>
    {
        public string ImplType { get; }
        public string InterfaceType { get; }
        public string RequestType { get; }
        public string ResponseType { get; }
        public ImmutableArray<DecoratorUsage> Decorators { get; }

        public HandlerInfo(string implType, string interfaceType, string requestType, string responseType, ImmutableArray<DecoratorUsage> decorators)
        {
            ImplType = implType;
            InterfaceType = interfaceType;
            RequestType = requestType;
            ResponseType = responseType;
            Decorators = decorators;
        }

        public bool Equals(HandlerInfo other) =>
            other != null &&
            ImplType == other.ImplType &&
            InterfaceType == other.InterfaceType &&
            Decorators.SequenceEqual(other.Decorators);

        public override bool Equals(object obj) => Equals(obj as HandlerInfo);
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 23 + ImplType.GetHashCode();
                hash = hash * 23 + InterfaceType.GetHashCode();
                return hash;
            }
        }
    }
}