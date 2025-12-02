namespace CleanMediator.Abstractions;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = true)]
public class DecoratorAttribute(Type decoratorType, int order = 0) : Attribute
{
    public Type DecoratorType { get; } = decoratorType;
    public int Order { get; set; } = order;
}