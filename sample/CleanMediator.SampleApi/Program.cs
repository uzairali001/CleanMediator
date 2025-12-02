using CleanMediator.Abstractions;
using CleanMediator.Generated;
using CleanMediator.SampleApi.Behaviors;

using FluentValidation;

using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();



// --- 1. Register Validators ---
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

// --- 2. Register Handlers & Decorate with Scrutor ---
builder.Services.Scan(scan => scan
    .FromAssemblyOf<Program>()
    // A. Register the base handlers
    .AddClasses(classes => classes.AssignableTo(typeof(ICommandHandler<,>)))
    .AsImplementedInterfaces()
    .WithScopedLifetime());

// B. Decorate them!
// First Decoration: Wrap the handler with Validation
// Result: Validation -> Handler
builder.Services.Decorate(typeof(ICommandHandler<,>), typeof(ValidationDecorator<,>));

// Second Decoration: Wrap the validator with Logging
// Result: Logging -> Validation -> Handler
builder.Services.Decorate(typeof(ICommandHandler<,>), typeof(LoggingDecorator<,>));

// --- 3. Register Notifications (Source Generated) ---
builder.Services.AddCleanMediator();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
