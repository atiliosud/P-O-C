using CaseAgents.Core.Abstractions;
using CaseAgents.Core.Models;
using CaseAgents.Eval;
using CaseAgents.Infra;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Usage: dotnet run --project src/CaseAgents.Eval -- --api http://localhost:5080
//    or: dotnet run --project src/CaseAgents.Eval -- --Storage:Mode=Cosmos (plus Storage:Cosmos:* settings)
var builder = Host.CreateApplicationBuilder(args);
var apiUrl = builder.Configuration["api"];
var useApi = !string.IsNullOrWhiteSpace(apiUrl);

if (!useApi && !string.Equals(builder.Configuration["Storage:Mode"], StorageModes.Cosmos, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Eval needs real data. Pass --api <url> (for example --api http://localhost:5080) or set Storage:Mode=Cosmos.");
    return 2;
}

builder.Services.AddCaseAgentsCore(builder.Configuration);
if (!useApi)
    builder.Services.AddCaseAgentsStorage(builder.Configuration);

using var host = builder.Build();
var questionnaire = host.Services.GetRequiredService<Questionnaire>();

IReadOnlyList<CaseRecord> records;
if (useApi)
{
    using var http = new HttpClient { BaseAddress = new Uri(apiUrl!) };
    records = await new ApiRecordLoader(http).LoadAsync();
}
else
{
    records = await host.Services.GetRequiredService<ICaseReader>().ListCaseRecordsAsync();
}

Console.WriteLine($"Cases: {records.Count}");
Console.WriteLine(EvalReport.Render(EvalCalculator.Compute(questionnaire, records)));
return 0;
