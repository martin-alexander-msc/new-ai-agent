
using System.Net.Http;
using System.Net.Http.Json;

var builder = WebApplication.CreateBuilder(args);
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

// MCP Agent endpoint using OpenAI
app.MapPost("/mcp-agent", async (HttpRequest request) =>
{
    // Read prompt from JSON body
    string body;
    using (var localReader = new StreamReader(request.Body))
    {
        body = await localReader.ReadToEndAsync();
    }
    var prompt = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("prompt").GetString();

    // Call local Ollama instance
    var ollamaUrl = "http://localhost:11434/api/chat";
    var ollamaRequest = new {
        model = "llama3.2",
        messages = new[] {
            new { role = "user", content = prompt }
        }
    };

    var httpContent = new StringContent(System.Text.Json.JsonSerializer.Serialize(ollamaRequest), System.Text.Encoding.UTF8, "application/json");

    using var httpClient = new HttpClient();
    var ollamaResponse = await httpClient.PostAsync(ollamaUrl, httpContent);
    if (!ollamaResponse.IsSuccessStatusCode)
    {
        return Results.Json(new {
            mcp_version = "1.0",
            agent = "ollama",
            input = prompt,
            output = $"Ollama error: {ollamaResponse.StatusCode}"
        });
    }
    var stringContent = await ollamaResponse.Content.ReadAsStringAsync();
    var responseBuilder = new System.Text.StringBuilder();
    using (var reader = new StringReader(stringContent))
    {
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            try
            {
                var resp = System.Text.Json.JsonSerializer.Deserialize<OllamaChatResponse>(line);
                if (resp?.message?.content != null)
                    responseBuilder.Append(resp.message.content);
            }
            catch { /* ignore parse errors for incomplete lines */ }
        }
    }
    var responseText = responseBuilder.ToString();

    // Return MCP format
    return Results.Json(new {
        mcp_version = "1.0",
        agent = "ollama",
        input = prompt,
        output = responseText
    });
});


// Helper for async line reading
static async IAsyncEnumerable<string> ReadLinesAsync(StreamReader reader)
{
    string? line;
    while ((line = await reader.ReadLineAsync()) != null)
    {
        yield return line;
    }
}

// MCP Agent streaming endpoint using Ollama

// MCP Agent streaming endpoint using Ollama
app.MapPost("/mcp-agent-stream", async (HttpRequest request, HttpResponse response) =>
{
    // Read prompt from JSON body
    string body;
    using (var localReader = new StreamReader(request.Body))
    {
        body = await localReader.ReadToEndAsync();
    }
    var prompt = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("prompt").GetString();

    // Call local Ollama instance
    var ollamaUrl = "http://localhost:11434/api/chat";
    var ollamaRequest = new {
        model = "llama3.2",
        messages = new[] {
            new { role = "user", content = prompt }
        }
    };
    var httpContent = new StringContent(System.Text.Json.JsonSerializer.Serialize(ollamaRequest), System.Text.Encoding.UTF8, "application/json");
    using var httpClient = new HttpClient();
    using var ollamaResponse = await httpClient.PostAsync(ollamaUrl, httpContent);
    response.ContentType = "application/jsonl";
    response.Headers.Append("X-Accel-Buffering", "no");
    response.Headers.Append("Cache-Control", "no-cache");
    using var stream = await ollamaResponse.Content.ReadAsStreamAsync();
    using var reader = new StreamReader(stream);
    await foreach (var line in ReadLinesAsync(reader))
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            await response.WriteAsync(line + "\n");
            await response.Body.FlushAsync();
        }
    }
});

// Simple in-memory RAG endpoint: context is the current time
app.MapPost("/mcp-agent-rag", async (HttpRequest request) =>
{
    // Read prompt from JSON body
    string body;
    using (var localReader = new StreamReader(request.Body))
    {
        body = await localReader.ReadToEndAsync();
    }
    var json = System.Text.Json.JsonDocument.Parse(body).RootElement;
    var prompt = json.GetProperty("prompt").GetString();

    // In-memory repository: context is current time, with explicit LLM instruction
    var context = $"The current local time is: {DateTime.Now:yyyy-MM-dd HH:mm:ss} in Lisbon.";
    var instruction = "If the user's question can be answered by inferring from the provided context (for example, by converting time zones), you should do so. Keep your answer concise and only provide the final result, do not provide your inference details unless asked. Otherwise, reply: 'Sorry, I am unable to answer your question based on the provided context.'";
    var ragPrompt = $"Context:\n{context}\n\nInstruction:\n{instruction}\n\nUser question:\n{prompt}";

    // Call local Ollama instance
    var ollamaUrl = "http://localhost:11434/api/chat";
    var ollamaRequest = new {
        model = "llama3.2",
        messages = new[] {
            new { role = "user", content = ragPrompt }
        }
    };
    var httpContent = new StringContent(System.Text.Json.JsonSerializer.Serialize(ollamaRequest), System.Text.Encoding.UTF8, "application/json");
    using var httpClient = new HttpClient();
    var ollamaResponse = await httpClient.PostAsync(ollamaUrl, httpContent);
    if (!ollamaResponse.IsSuccessStatusCode)
    {
        return Results.Json(new {
            mcp_version = "1.0",
            agent = "ollama-rag",
            input = prompt,
            context = context,
            output = $"Ollama error: {ollamaResponse.StatusCode}"
        });
    }
    var stringContent = await ollamaResponse.Content.ReadAsStringAsync();
    var responseBuilder = new System.Text.StringBuilder();
    using (var reader = new StringReader(stringContent))
    {
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            try
            {
                var resp = System.Text.Json.JsonSerializer.Deserialize<OllamaChatResponse>(line);
                if (resp?.message?.content != null)
                    responseBuilder.Append(resp.message.content);
            }
            catch { /* ignore parse errors for incomplete lines */ }
        }
    }
    var responseText = responseBuilder.ToString();

    // Return MCP format
    return Results.Json(new {
        mcp_version = "1.0",
        agent = "ollama-rag",
        input = prompt,
        context = context,
        output = responseText
    });
});

app.Run();


// Helper class for Ollama response

public class OllamaChatResponse
{
    public OllamaMessage? message { get; set; }
}
public class OllamaMessage
{
    public string? role { get; set; }
    public string? content { get; set; }
}

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
