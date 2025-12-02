using CleanMediator.SampleApi;

using FluentValidation;

using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddMemoryCache();

// --- 1. Register Infrastructure ---
// Register the Exception Handler
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails(); // Standard problem details support

// --- 2. Register Validators ---
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

//builder.Services.AddCleanMediator();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();

app.UseAuthorization();

// Enable the Exception Handler Middleware
app.UseExceptionHandler();

app.MapControllers();

app.Run();
