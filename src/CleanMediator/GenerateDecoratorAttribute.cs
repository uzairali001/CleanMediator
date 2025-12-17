namespace CleanMediator.Abstractions;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class GenerateDecoratorAttribute(string name) : Attribute
{
    // The name of the attribute to generate (e.g., "Cached" -> "CachedAttribute")
    public string Name { get; } = name;
}