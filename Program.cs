using NgrokAspNet;
using Twilio.TwiML;

var builder = WebApplication.CreateBuilder(args);
if (builder.Environment.IsDevelopment())
    builder.Services.AddHostedService<TunnelService>();

var app = builder.Build();

app.MapGet("/", () => "Hello World!");
app.MapPost("/voice", () =>
{
    var response = new VoiceResponse();
    response.Say("Hello World!");
    return Results.Text(response.ToString(), "text/xml");
});
app.MapPost("/message", () =>
{
    var response = new MessagingResponse();
    response.Message("Hello World!");
    return Results.Text(response.ToString(), "text/xml");
});

app.Run();