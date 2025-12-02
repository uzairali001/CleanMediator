using System.Reflection;

using CleanMediator.Abstractions;
using CleanMediator.Generators;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

public static class TestHelper
{
    public static Task<(GeneratorDriverRunResult RunResult, Compilation Compilation)> Verify(string source)
    {
        // 1. Parse the input source code
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        // 2. Create references needed for compilation
        // We need System.Runtime and our Abstractions library
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location), // System.Private.CoreLib
            MetadataReference.CreateFromFile(typeof(INotificationHandler<>).Assembly.Location), // CleanMediator.Abstractions
            MetadataReference.CreateFromFile(typeof(IServiceProvider).Assembly.Location), // Microsoft.Extensions.DependencyInjection.Abstractions
            MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location)
        };

        // 3. Create the Compilation (Simulates the compiler)
        var compilation = CSharpCompilation.Create(
            assemblyName: "Tests",
            syntaxTrees: new[] { syntaxTree },
            references: references);

        // 4. Create an instance of our Generator
        var generator = new EventPublisherGenerator();

        // 5. Run the Generator
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGenerators(compilation);

        // 6. Return results
        return Task.FromResult((driver.GetRunResult(), compilation));
    }
}