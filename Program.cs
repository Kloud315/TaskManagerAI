using TaskManagerAI.Data;
using TaskManagerAI.Configuration;
using TaskManagerAI.Middleware;
using TaskManagerAI.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddScoped<ITaskRepository>(services =>
{
    var connectionString = services.GetRequiredService<IConfiguration>()
        .GetConnectionString("DefaultConnection");

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException(
            "The 'DefaultConnection' connection string is not configured.");
    }

    return new TaskRepository(connectionString);
});
builder.Services.AddScoped<ITaskService, TaskService>();
builder.Services.Configure<GeminiOptions>(
    builder.Configuration.GetSection(GeminiOptions.SectionName));
builder.Services.AddHttpClient<IGeminiClient, GeminiClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddScoped<ToolExecutor>();
builder.Services.AddSingleton<DeleteConfirmationStore>();
builder.Services.AddScoped<AssistantService>();

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseMiddleware<GlobalExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
